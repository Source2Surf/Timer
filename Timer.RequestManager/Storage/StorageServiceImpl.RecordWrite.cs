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

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<(EAttemptResult, RunRecord, int rank)> AddPlayerRecord(SteamID steamId, string mapName, RecordRequest recordRequest)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);

        var styleValue = recordRequest.Style;
        var trackValue = ToUInt16(recordRequest.Track);
        await EnsureBestRunsSeededAsync(mapId, RunType.Main, styleValue, trackValue, 0);

        var now = DateTime.UtcNow;
        var run = CreateRunEntity(steamId, mapId, recordRequest, now);
        run.RunType = RunType.Main;
        run.Stage   = 0;

        var result = EAttemptResult.NoNewRecord;
        var wakeScoreRecalcWorker = false;
        await WithRecordTransactionAsync(async () =>
        {
            // WithRecordTransactionAsync can retry a rolled-back lock conflict. Keep the post-
            // commit wake tied to the final successful attempt, not an earlier rolled-back one.
            wakeScoreRecalcWorker = false;
            await LockMapAsync(mapId);
            var write = await WriteRunInCurrentRecordTransactionAsync(
                run,
                runId => CreateRunSegmentsFromCheckpoints(runId, recordRequest, now),
                recordRequest.StyleFactor,
                enqueueScoreRecalc: true);
            result = write.AttemptResult;
            wakeScoreRecalcWorker = write.WakeScoreRecalcWorker;
        });

        if (wakeScoreRecalcWorker)
        {
            // Only wake after commit. The durable outbox row above is the source of truth.
            WakeScoreRecalcWorker();
        }

        // Post-commit, OUTSIDE the rollback-guarded try: the run + best row are now durably saved. The rank
        // COUNT is advisory — if a transient DB error throws here it must NOT roll
        // back the committed transaction nor surface the finish as a failed save, so it is logged and the
        // record is still returned (rank falls back to 0). Match the leaderboard and points
        // ordering: time first, then the earlier RunId for equal times.
        var rank = await ComputePostCommitRankAsync(
            result, mapId, styleValue, trackValue, run.Id,
            (m, s, t, id) => QueryMainRunRankAsync(m, s, t, id));

        return (result, ToRunRecord(run), rank);
    }

    private async Task<int> ComputePostCommitRankAsync(
        EAttemptResult result,
        ulong          mapId,
        int            styleValue,
        ushort         trackValue,
        ulong          runId,
        Func<ulong, int, ushort, ulong, Task<int>> rankQuery)
    {
        try
        {
            var rank = result switch
            {
                EAttemptResult.NewServerRecord   => 1,
                EAttemptResult.NewPersonalRecord => await rankQuery(mapId, styleValue, trackValue, runId),
                _                                => 0,
            };

            return rank;
        }
        catch (Exception e)
        {
            // The record is already committed; never fail the save over advisory post-commit work.
            _logger.LogWarning(e, "Post-commit rank query failed for map {MapId} style {Style} track {Track}; record was saved.",
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
            var write = await WriteRunInCurrentRecordTransactionAsync(
                run,
                runId => CreateRunSegmentsFromCheckpoints(runId, newRunRecord, now),
                styleFactor: 1,
                enqueueScoreRecalc: false);
            result = write.AttemptResult;
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
                rank = await QueryStageRunRankAsync(mapId, styleValue, trackValue, stageValue, run.Id);
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
            // Keep submission receipts: an exact retry acknowledges the historical commit,
            // rather than recreating a run intentionally removed by this administrative wipe.
            // Capture and deduplicate affected players before deleting their track scores.
            var affectedRows = await _db.Queryable<PlayerTrackScoreEntity>()
                                        .Where(x => x.MapId == mapId.Value)
                                        .GroupBy(x => x.SteamId)
                                        .Select(x => new PlayerIdRow { SteamId = x.SteamId })
                                        .ToListAsync(OperationCancellation);

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
                                  .ToListAsync(OperationCancellation);

            const int runIdChunkSize = 5000;

            for (var offset = 0; offset < runIds.Count; offset += runIdChunkSize)
            {
                var chunk = runIds.GetRange(offset, Math.Min(runIdChunkSize, runIds.Count - offset));

                await _db.Deleteable<RunSegmentEntity>()
                         .Where(segment => chunk.Contains(segment.RunId))
                         .ExecuteCommandAsync(OperationCancellation);
            }

            await _db.Deleteable<RunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync(OperationCancellation);

            await _db.Deleteable<ReplayEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync(OperationCancellation);

            await _db.Deleteable<PlayerBestRunEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync(OperationCancellation);

            // Track scores are NOT cascade-deleted with runs/best-runs; without this they linger and keep
            // inflating PlayerEntity.Points forever, since RecalculateTrackScoresAsync early-returns on an
            // empty track and never cleans them up. (PlayerMapStats playtime/playcount is deliberately left
            // intact — it is play-session telemetry, not a run record, and survived record-clears at HEAD.)
            await _db.Deleteable<PlayerTrackScoreEntity>()
                     .Where(x => x.MapId == mapId.Value)
                     .ExecuteCommandAsync(OperationCancellation);

            await UpdatePlayerTotalPointsAsync(affectedPlayers);
        });
        RemoveBestRunSeedCacheForMap(mapId.Value);
    }

    /// <summary>
    /// Shared transaction-local write core for plugin-originated records and authoritative
    /// backend submissions. The caller owns the map lock; this method never seeds historical
    /// best-runs or performs a rank COUNT, preserving the backend write path's bounded lock.
    /// </summary>
    private async Task<RunWriteOutcome> WriteRunInCurrentRecordTransactionAsync(
        RunEntity run,
        Func<ulong, List<RunSegmentEntity>> createSegments,
        double styleFactor,
        bool enqueueScoreRecalc)
    {
        if (_db.Ado.Transaction is null)
        {
            throw new InvalidOperationException("Run writes require an active record transaction.");
        }

        var bestTimes = run.RunType == RunType.Main
            ? await QueryMainBestTimesAsync(run.SteamId, run.MapId, run.Style, run.Track)
            : await QueryStageBestTimesAsync(run.SteamId, run.MapId, run.Style, run.Track, run.Stage);
        var result = ResolveAttemptResult(run.Time, bestTimes?.ServerBestTime, bestTimes?.PlayerBestTime);
        run.Id = unchecked((ulong)await _db.Insertable(run).ExecuteReturnBigIdentityAsync(OperationCancellation));

        var segments = createSegments(run.Id);
        if (segments.Count > 0)
        {
            await _db.Insertable(segments).ExecuteCommandAsync(OperationCancellation);
        }

        await UpsertPlayerBestRunAsync(run, bestTimes);

        if (enqueueScoreRecalc && result != EAttemptResult.NoNewRecord)
        {
            // This merge is part of the same ReadCommitted map-locked transaction as the
            // run and best-run projection. A committed PB/WR can therefore never lose its
            // durable score recalculation request.
            await EnqueueScoreRecalcInCurrentRecordTransactionAsync(
                run.MapId, run.Style, run.Track, styleFactor, DateTime.UtcNow);
            return new RunWriteOutcome(result, WakeScoreRecalcWorker: true);
        }

        return new RunWriteOutcome(result, WakeScoreRecalcWorker: false);
    }

    private Task<AttemptBestTimesRow?> QueryMainBestTimesAsync(SteamID steamId,
                                                                ulong   mapId,
                                                                int     style,
                                                                ushort  track)
        => QueryMainBestTimesAsync(ToDbSteamId(steamId), mapId, style, track);

    private async Task<AttemptBestTimesRow?> QueryMainBestTimesAsync(long steamIdValue,
                                                                      ulong mapId,
                                                                      int style,
                                                                      ushort track)
    {
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
                                    .FirstAsync(OperationCancellation);
    }

    private async Task<int> QueryMainRunRankAsync(ulong mapId, int style, ushort track, ulong runId)
    {
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, stage);

        // Compare persisted floats to each other. A bound float parameter can be
        // promoted to double by MySQL, making even a run compare as faster than itself.
        var precedingCount = await QueryBestRuns()
            .InnerJoin<RunEntity>((best, saved) => saved.Id == runId)
            .Where((best, saved) => best.MapId == mapId
                                   && best.RunType == RunType.Main
                                   && best.Style == style && best.Track == track && best.Stage == stage
                                   && (best.BestTime < saved.Time
                                       || (best.BestTime == saved.Time && best.RunId < saved.Id)))
            .CountAsync(OperationCancellation);

        return precedingCount + 1;
    }

    private Task<AttemptBestTimesRow?> QueryStageBestTimesAsync(SteamID steamId,
                                                                 ulong   mapId,
                                                                 int     style,
                                                                 ushort  track,
                                                                 ushort  stage)
        => QueryStageBestTimesAsync(ToDbSteamId(steamId), mapId, style, track, stage);

    private async Task<AttemptBestTimesRow?> QueryStageBestTimesAsync(long steamIdValue,
                                                                       ulong mapId,
                                                                       int style,
                                                                       ushort track,
                                                                       ushort stage)
    {
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
                                    .FirstAsync(OperationCancellation);
    }

    private async Task<int> QueryStageRunRankAsync(ulong    mapId,
                                                    int      style,
                                                    ushort   track,
                                                    ushort   stage,
                                                    ulong    runId)
    {
        await EnsureBestRunsSeededAsync(mapId, RunType.Stage, style, track, stage);

        // Compare persisted floats to each other. A bound float parameter can be
        // promoted to double by MySQL, making even a run compare as faster than itself.
        var precedingCount = await QueryBestRuns()
            .InnerJoin<RunEntity>((best, saved) => saved.Id == runId)
            .Where((best, saved) => best.MapId == mapId
                                   && best.RunType == RunType.Stage
                                   && best.Style == style && best.Track == track && best.Stage == stage
                                   && (best.BestTime < saved.Time
                                       || (best.BestTime == saved.Time && best.RunId < saved.Id)))
            .CountAsync(OperationCancellation);

        return precedingCount + 1;
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
            DateUnixTimeMilliseconds = ToUnixTimeMilliseconds(now),
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

    private readonly record struct RunWriteOutcome(EAttemptResult AttemptResult, bool WakeScoreRecalcWorker);
}
