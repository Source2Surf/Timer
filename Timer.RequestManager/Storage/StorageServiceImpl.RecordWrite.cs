using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID steamId, string mapName, RecordRequest recordRequest)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        var styleValue = recordRequest.Style;
        var trackValue = ToUInt16(recordRequest.Track);
        await EnsureBestRunsSeededAsync(mapId, RunType.Main, styleValue, trackValue, 0);

        var run = CreateRunEntity(steamId, mapId, recordRequest, DateTime.UtcNow);
        run.RunType = RunType.Main;
        run.Stage   = 0;

        var result = EAttemptResult.NoNewRecord;
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            var bestTimes = await QueryMainBestTimesAsync(steamId, mapId, styleValue, trackValue);
            result = ResolveAttemptResult(recordRequest.Time, bestTimes?.ServerBestTime, bestTimes?.PlayerBestTime);
            var newRunId = await _db.Insertable(run).ExecuteReturnBigIdentityAsync();
            run.Id = unchecked((ulong) newRunId);

            if (recordRequest.Checkpoints.Count > 0)
            {
                var stageSegments = CreateRunSegmentsFromCheckpoints(run.Id, recordRequest, run.Date);

                if (stageSegments.Count > 0)
                {
                    await _db.Insertable(stageSegments).ExecuteCommandAsync();
                }
            }

            await UpsertPlayerBestRunAsync(run, bestTimes);
        });

        // Post-commit, OUTSIDE the rollback-guarded try: the run + best row are now durably saved. The rank
        // COUNT is advisory — if a transient DB error throws here it must NOT roll
        // back the committed transaction nor surface the finish as a failed save, so it is logged and the
        // record is still returned (rank falls back to 0). The just-written best row (BestTime == run.Time)
        // is excluded by the strict `BestTime < run.Time` rank predicate, so the post-commit count is exact.
        var rank = await ComputePostCommitRankAndEnqueueRecalcAsync(
            result, mapId, styleValue, trackValue, run.Time, recordRequest.StyleFactor,
            (m, s, t, time) => QueryMainRunRankAsync(m, s, t, time));

        return (result, ToRunRecord(run), rank);
    }

    private async Task<int> ComputePostCommitRankAndEnqueueRecalcAsync(
        EAttemptResult result,
        ulong          mapId,
        int            styleValue,
        ushort         trackValue,
        float          runTime,
        double         styleFactor,
        Func<ulong, int, ushort, float, Task<int>> rankQuery)
    {
        // Queue first: an advisory rank-query failure must not lose the score update.
        if (result != EAttemptResult.NoNewRecord)
            _scoreRecalcScheduler.Enqueue(new RecalcRequest(mapId, styleValue, trackValue, styleFactor));

        try
        {
            var rank = result switch
            {
                EAttemptResult.NewServerRecord   => 1,
                EAttemptResult.NewPersonalRecord => await rankQuery(mapId, styleValue, trackValue, runTime),
                _                                => 0,
            };

            return rank;
        }
        catch (Exception e)
        {
            // The record is already committed; never fail the save over advisory post-commit work.
            _logger.LogWarning(e, "Post-commit rank/recalc failed for map {MapId} style {Style} track {Track}; record was saved.",
                               mapId, styleValue, trackValue);

            return 0;
        }
    }

    public async Task<(EAttemptResult, RunRecord, int rank)> AddPlayerStageRecord(SteamID steamId, string mapName, RecordRequest newRunRecord)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        var styleValue = newRunRecord.Style;
        var trackValue = ToUInt16(newRunRecord.Track);
        var stageValue = ToUInt16(newRunRecord.Stage);
        await EnsureBestRunsSeededAsync(mapId, RunType.Stage, styleValue, trackValue, stageValue);

        var now = DateTime.UtcNow;
        var run = CreateRunEntity(steamId, mapId, newRunRecord, now);
        run.RunType = RunType.Stage;
        run.Stage   = stageValue;

        var result = EAttemptResult.NoNewRecord;
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            var bestTimes = await QueryStageBestTimesAsync(steamId, mapId, styleValue, trackValue, stageValue);
            result = ResolveAttemptResult(newRunRecord.Time, bestTimes?.ServerBestTime, bestTimes?.PlayerBestTime);
            var runId = await _db.Insertable(run).ExecuteReturnBigIdentityAsync();
            run.Id = unchecked((ulong) runId);

            // Persist stage checkpoints like the main path does — without this the LiteDB
            // backend returns checkpoint data for stage records while SQL returns [].
            if (newRunRecord.Checkpoints.Count > 0)
            {
                var stageSegments = CreateRunSegmentsFromCheckpoints(run.Id, newRunRecord, now);

                if (stageSegments.Count > 0)
                {
                    await _db.Insertable(stageSegments).ExecuteCommandAsync();
                }
            }

            await UpsertPlayerBestRunAsync(run, bestTimes);
        });

        // Post-commit advisory rank, OUTSIDE the rollback-guarded try (see AddPlayerRecord): the stage run
        // is durably saved; a transient failure on the COUNT must not roll back or report a failed save.
        var rank = 0;

        if (result == EAttemptResult.NewServerRecord)
        {
            rank = 1;
        }
        else if (result == EAttemptResult.NewPersonalRecord)
        {
            try
            {
                rank = await QueryStageRunRankAsync(mapId, styleValue, trackValue, stageValue, run.Time);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Post-commit stage rank failed for map {MapId} style {Style} track {Track} stage {Stage}; record was saved.",
                                   mapId, styleValue, trackValue, stageValue);
            }
        }

        return (result, ToRunRecord(run), rank);
    }

    public async Task RemoveMapRecords(string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return;
        }

        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId.Value);
            // Capture and deduplicate affected players before deleting their track scores.
            var affectedRows = await _db.Queryable<PlayerTrackScoreEntity>()
                                        .Where(x => x.MapId == mapId.Value)
                                        .GroupBy(x => x.SteamId)
                                        .Select(x => new PlayerIdRow { SteamId = x.SteamId })
                                        .ToListAsync();

            var affectedPlayers = new List<long>(affectedRows.Count);
            foreach (var row in affectedRows)
            {
                affectedPlayers.Add(row.SteamId);
            }

            await LockPlayersForPointsAsync(affectedPlayers);

            // Delete segments by materialized run-id list instead of a correlated-EXISTS
            // delete: MySQL does not semi-join-transform DELETE, so EXISTS would evaluate
            // per segment row (a full scan of the largest table while this transaction
            // holds its locks). The id read uses the MapId index and the deletes hit
            // idx_surf_runs_segments_runid_stage. Chunked to bound statement size — the
            // IN-list renders as inlined literals.
            var runIds = await _db.Queryable<RunEntity>()
                                  .Where(run => run.MapId == mapId.Value)
                                  .Select(run => run.Id)
                                  .ToListAsync();

            const int runIdChunkSize = 5000;

            for (var offset = 0; offset < runIds.Count; offset += runIdChunkSize)
            {
                var chunk = runIds.GetRange(offset, Math.Min(runIdChunkSize, runIds.Count - offset));

                await _db.Deleteable<RunSegmentEntity>()
                         .Where(segment => chunk.Contains(segment.RunId))
                         .ExecuteCommandAsync();
            }

            await _db.Deleteable<RunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await _db.Deleteable<ReplayEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await _db.Deleteable<PlayerBestRunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            // Track scores are NOT cascade-deleted with runs/best-runs; without this they linger and keep
            // inflating PlayerEntity.Points forever, since RecalculateTrackScoresAsync early-returns on an
            // empty track and never cleans them up. (PlayerMapStats playtime/playcount is deliberately left
            // intact — it is play-session telemetry, not a run record, and survived record-clears at HEAD.)
            await _db.Deleteable<PlayerTrackScoreEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync();

            await UpdatePlayerTotalPointsAsync(affectedPlayers);
        });
        RemoveBestRunSeedCacheForMap(mapId.Value);
    }

    private async Task<AttemptBestTimesRow?> QueryMainBestTimesAsync(SteamID steamId,
                                                                      ulong   mapId,
                                                                      int     style,
                                                                      ushort  track)
    {
        var steamIdValue = ToDbSteamId(steamId);
        const ushort stage = 0;

        return await QueryBestRuns().Where(x => x.MapId == mapId
                                                && x.RunType == RunType.Main
                                                && x.Style == style
                                                && x.Track == track
                                                && x.Stage == stage)
                                    .Select(x => new AttemptBestTimesRow
                                    {
                                        PlayerBestRowId = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue, (ulong?)x.Id, null)),
                                        PlayerBestRunId = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue, (ulong?)x.RunId, null)),
                                        ServerBestTime = SqlFunc.AggregateMin(x.BestTime),
                                        PlayerBestTime = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue,
                                                                                           (float?) x.BestTime,
                                                                                           null)),
                                    })
                                    .FirstAsync();
    }

    private async Task<int> QueryMainRunRankAsync(ulong mapId, int style, ushort track, float runTime)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        var quickerCount = await QueryBestRuns().Where(run => run.MapId == mapId
                                                              && run.RunType == RunType.Main
                                                              && run.Style == style
                                                              && run.Track == track
                                                              && run.Stage == stage
                                                              && run.BestTime < runTime)
                                        .CountAsync();

        return quickerCount + 1;
    }

    private async Task<AttemptBestTimesRow?> QueryStageBestTimesAsync(SteamID steamId,
                                                                       ulong   mapId,
                                                                       int     style,
                                                                       ushort  track,
                                                                       ushort  stage)
    {
        var steamIdValue = ToDbSteamId(steamId);
        return await QueryBestRuns().Where(x => x.MapId == mapId
                                                && x.RunType == RunType.Stage
                                                && x.Style == style
                                                && x.Track == track
                                                && x.Stage == stage)
                                    .Select(x => new AttemptBestTimesRow
                                    {
                                        PlayerBestRowId = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue, (ulong?)x.Id, null)),
                                        PlayerBestRunId = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue, (ulong?)x.RunId, null)),
                                        ServerBestTime = SqlFunc.AggregateMin(x.BestTime),
                                        PlayerBestTime = SqlFunc.AggregateMin(SqlFunc.IIF(x.SteamId == steamIdValue,
                                                                                           (float?) x.BestTime,
                                                                                           null)),
                                    })
                                    .FirstAsync();
    }

    private async Task<int> QueryStageRunRankAsync(ulong    mapId,
                                                    int      style,
                                                    ushort   track,
                                                    ushort   stage,
                                                    float    runTime)
    {
        await EnsureBestRunsSeededAsync(mapId, RunType.Stage, style, track, stage);

        var quickerCount = await QueryBestRuns().Where(run => run.MapId == mapId
                                                              && run.RunType == RunType.Stage
                                                              && run.Style == style
                                                              && run.Track == track
                                                              && run.Stage == stage
                                                              && run.BestTime < runTime)
                                        .CountAsync();

        return quickerCount + 1;
    }

    private static RunEntity CreateRunEntity(SteamID steamId, ulong mapId, RecordRequest request, DateTime now)
        => new ()
        {
            SteamId        = ToDbSteamId(steamId),
            MapId          = mapId,
            RunType        = request.Stage > 0 ? RunType.Stage : RunType.Main,
            Stage          = ToUInt16(request.Stage),
            Style          = request.Style,
            Track          = ToUInt16(request.Track),
            Time           = request.Time,
            Jumps          = ToUInt32(request.Jumps),
            Strafes        = ToUInt32(request.Strafes),
            Sync           = request.Sync,
            VelocityStartX = request.VelocityStartX,
            VelocityStartY = request.VelocityStartY,
            VelocityStartZ = request.VelocityStartZ,
            VelocityEndX   = request.VelocityEndX,
            VelocityEndY   = request.VelocityEndY,
            VelocityEndZ   = request.VelocityEndZ,
            VelocityMaxX   = request.VelocityMaxX,
            VelocityMaxY   = request.VelocityMaxY,
            VelocityMaxZ   = request.VelocityMaxZ,
            VelocityAvgX   = request.VelocityAvgX,
            VelocityAvgY   = request.VelocityAvgY,
            VelocityAvgZ   = request.VelocityAvgZ,
            Date           = now,
        };

    private static List<RunSegmentEntity> CreateRunSegmentsFromCheckpoints(ulong runId, RecordRequest request, DateTime now)
    {
        if (request.Checkpoints.Count == 0)
        {
            return [];
        }

        var segments = new List<RunSegmentEntity>(request.Checkpoints.Count);

        foreach (var checkpoint in request.Checkpoints)
        {
            segments.Add(new ()
            {
                RunId          = runId,
                Stage          = ToUInt16(checkpoint.CheckpointIndex),
                Time           = checkpoint.Time,
                Jumps          = ToUInt32(request.Jumps),
                Strafes        = ToUInt32(request.Strafes),
                Sync           = checkpoint.Sync,
                VelocityStartX = checkpoint.VelocityStartX,
                VelocityStartY = checkpoint.VelocityStartY,
                VelocityStartZ = checkpoint.VelocityStartZ,
                VelocityEndX   = checkpoint.VelocityEndX,
                VelocityEndY   = checkpoint.VelocityEndY,
                VelocityEndZ   = checkpoint.VelocityEndZ,
                VelocityMaxX   = checkpoint.VelocityMaxX,
                VelocityMaxY   = checkpoint.VelocityMaxY,
                VelocityMaxZ   = checkpoint.VelocityMaxZ,
                VelocityAvgX   = checkpoint.VelocityAvgX,
                VelocityAvgY   = checkpoint.VelocityAvgY,
                VelocityAvgZ   = checkpoint.VelocityAvgZ,
                Date           = now,
            });
        }

        return segments;
    }

    private static EAttemptResult ResolveAttemptResult(float newTime, float? serverBestTime, float? playerBestTime)
    {
        if (serverBestTime is null || newTime < serverBestTime.Value)
        {
            return EAttemptResult.NewServerRecord;
        }

        if (playerBestTime is null || newTime < playerBestTime.Value)
        {
            return EAttemptResult.NewPersonalRecord;
        }

        return EAttemptResult.NoNewRecord;
    }

    private sealed class PlayerIdRow
    {
        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }
    }
}
