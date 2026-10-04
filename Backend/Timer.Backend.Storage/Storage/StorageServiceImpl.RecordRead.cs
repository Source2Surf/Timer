using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        await EnsureBestRunsSeededForMapAsync(mapId.Value, RunType.Main);

        var rows = await QueryBestMainRunsForPlayerAsync(steamId, mapId.Value);

        var result = new List<RunRecord>(rows.Count);

        foreach (var run in rows)
        {
            result.Add(ToRunRecord(run));
        }

        return result;
    }

    public async Task<RunRecord?> GetPlayerRecord(SteamID steamId, string mapName, int style, int track)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return null;
        }

        var trackValue = ToUInt16(track);
        const ushort stage = 0;

        await EnsureBestRunsSeededAsync(mapId.Value, RunType.Main, style, trackValue, stage);

        var record = await QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                                          .Where((best, run) => best.MapId == mapId.Value
                                                                && best.RunType == RunType.Main
                                                                && best.Stage == stage
                                                                && best.SteamId == steamIdValue
                                                                && best.Style == style
                                                                && best.Track == trackValue)
                                          .OrderBy((best, run) => best.BestTime)
                                          .OrderBy((best, run) => best.RunId)
                                          .Select((best, run) => run)
                                          .FirstAsync(OperationCancellation);

        return record is null ? null : ToRunRecord(record);
    }

    public async Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        await EnsureBestRunsSeededForMapAsync(mapId.Value, RunType.Stage);

        var runs = await QueryBestStageRunsForPlayerAsync(steamId, mapId.Value);

        var result = new List<RunRecord>(runs.Count);

        foreach (var run in runs)
        {
            result.Add(ToRunRecord(run));
        }

        return result;
    }

    /// <summary>
    /// Bounded read-model query for the standalone HTTP backend. Unlike the historical
    /// IRequestManager methods, read repair is explicit so replicas can use credentials
    /// that have SELECT permission only.
    /// </summary>
    internal async Task<IReadOnlyList<RunRecord>> GetPlayerRecordsForReadApiAsync(ulong  steamId,
                                                                                  string mapName,
                                                                                  bool   stageRecords,
                                                                                  int    limit,
                                                                                  bool   allowReadRepair)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        var runType = stageRecords ? RunType.Stage : RunType.Main;

        if (allowReadRepair)
        {
            await EnsureBestRunsSeededForMapAsync(mapId.Value, runType);
        }

        var steamIdValue = unchecked((long)steamId);
        var query = QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                                   .Where((best, run) => best.MapId == mapId.Value
                                                         && best.RunType == runType
                                                         && best.SteamId == steamIdValue);

        if (stageRecords)
        {
            query = query.Where((best, run) => best.Stage > 0)
                         .OrderBy((best, run) => best.Stage)
                         .OrderBy((best, run) => best.BestTime)
                         .OrderBy((best, run) => best.RunId);
        }
        else
        {
            query = query.Where((best, run) => best.Stage == 0)
                         .OrderBy((best, run) => best.BestTime)
                         .OrderBy((best, run) => best.RunId);
        }

        var runs = await query.Select((best, run) => run)
                              .Take(NormalizeLimit(limit))
                              .ToListAsync(OperationCancellation);
        var result = new List<RunRecord>(runs.Count);

        foreach (var run in runs)
        {
            result.Add(ToRunRecord(run));
        }

        await PopulatePlayerNamesAsync(result);

        return result;
    }

    public async Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var now = DateTime.UtcNow;

        var player = await _db.Queryable<PlayerEntity>()
                              .Where(x => x.SteamId == steamIdValue)
                              .FirstAsync(OperationCancellation);

        if (player is null)
        {
            player = new ()
            {
                SteamId   = ToDbSteamId(steamId),
                Name      = name,
                Points    = 0,
                Runs      = 0,
                JoinedAtUtc = now,
                UpdatedAt = now,
            };

            try
            {
                player.Id = checked((ulong)await _db.Insertable(player).ExecuteReturnBigIdentityAsync(OperationCancellation));
                // Return the database's timestamp precision on the first response too.
                player = await _db.Queryable<PlayerEntity>()
                                  .Where(x => x.Id == player.Id).FirstAsync(OperationCancellation);
            }
            catch (Exception ex) when (IsUniqueKeyViolation(ex))
            {
                // Another server may create the profile after our first read.
                // This insert is in autocommit, so PG can safely read the winner.
                player = await _db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamIdValue).FirstAsync(OperationCancellation);
                if (player is null) throw;
            }
        }

        if (player.JoinedAtUtc is null)
        {
            // Freeze the stored pre-update timestamp atomically. A competing login or
            // points writer may have initialized it since our first read.
            await _db.Updateable<PlayerEntity>()
                     .SetColumns(x => x.JoinedAtUtc == x.UpdatedAt)
                     .Where(x => x.Id == player.Id && x.JoinedAtUtc == null)
                     .ExecuteCommandAsync(OperationCancellation);
            player = await _db.Queryable<PlayerEntity>()
                              .Where(x => x.Id == player.Id).FirstAsync(OperationCancellation);
        }

        // Only write back if name changed
        if (player.Name != name)
        {
            player.Name      = name;
            player.UpdatedAt = now;

            await _db.Updateable(player)
                     .UpdateColumns(x => new { x.Name, x.UpdatedAt })
                     .ExecuteCommandAsync(OperationCancellation);
        }

        var profile = new PlayerProfile
        {
            Id           = (long) player.Id,
            SteamId      = steamId,
            Points       = player.Points,
            JoinDate     = player.JoinedAtUtc ?? throw new InvalidOperationException("Player join date was not initialized."),
            LastSeenDate = now,
        };

        profile.UpdateName(name);

        return profile;
    }

    public Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
        => GetPlayerPointsRankByDbIdAsync(ToDbSteamId(steamId));

    internal Task<(int rank, int total)> GetPlayerPointsRankForReadApiAsync(ulong steamId)
        => GetPlayerPointsRankByDbIdAsync(unchecked((long)steamId));

    private async Task<(int rank, int total)> GetPlayerPointsRankByDbIdAsync(long steamIdValue)
    {
        // Scalar projection (Points is a plain uint — no SteamID-converter concern);
        // no row and zero points both come back as 0.
        var playerPoints = await _db.Queryable<PlayerEntity>()
                                    .Where(x => x.SteamId == steamIdValue)
                                    .Select(x => x.Points)
                                    .FirstAsync(OperationCancellation);

        if (playerPoints == 0)
        {
            return (0, 0);
        }

        // Single query: COUNT(*) for total, SUM(CASE) for rank. The player's points were read in a
        // separate statement, so a concurrent recalculation can commit in between. Count the
        // player's own row in this same snapshot and never count them as ahead of themselves, so
        // the result is always consistent (rank <= total) instead of e.g. "rank 6 of 5".
        var stats = await _db.Queryable<PlayerEntity>()
                             .Where(x => x.Points > 0)
                             .Select(_ => new
                             {
                                 Total = SqlFunc.AggregateCount(_.Id),
                                 Ahead = SqlFunc.AggregateSum(SqlFunc.IIF(_.Points > playerPoints && _.SteamId != steamIdValue, 1, 0)),
                                 Self  = SqlFunc.AggregateSum(SqlFunc.IIF(_.SteamId == steamIdValue, 1, 0)),
                             })
                             .FirstAsync(OperationCancellation);

        if (stats is null || stats.Self == 0)
        {
            // Dropped to zero points after the first read: report it like any unranked player.
            return (0, 0);
        }

        return (stats.Ahead + 1, stats.Total);
    }

    public async Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId)
    {
        var runId = (ulong) recordId;

        var segments = await _db.Queryable<RunSegmentEntity>()
                                .Where(s => s.RunId == runId)
                                .OrderBy(s => s.Stage)
                                .ToListAsync(OperationCancellation);

        var result = new List<RunCheckpoint>(segments.Count);

        foreach (var seg in segments)
        {
            var cp = new RunCheckpoint
            {
                Id              = (long) seg.Id,
                RecordId        = recordId,
                CheckpointIndex = seg.Stage,
                Time            = seg.Time,
                Sync            = seg.Sync,
                VelocityStartX  = seg.VelocityStartX,
                VelocityStartY  = seg.VelocityStartY,
                VelocityStartZ  = seg.VelocityStartZ,
                VelocityEndX    = seg.VelocityEndX,
                VelocityEndY    = seg.VelocityEndY,
                VelocityEndZ    = seg.VelocityEndZ,
                VelocityMaxX    = seg.VelocityMaxX,
                VelocityMaxY    = seg.VelocityMaxY,
                VelocityMaxZ    = seg.VelocityMaxZ,
                VelocityAvgX    = seg.VelocityAvgX,
                VelocityAvgY    = seg.VelocityAvgY,
                VelocityAvgZ    = seg.VelocityAvgZ,
            };

            result.Add(cp);
        }

        return result;
    }

    private async Task<List<RunEntity>> QueryBestMainRunsForPlayerAsync(SteamID steamId, ulong mapId)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var results = await QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                            .Where((best, run) => best.MapId == mapId
                                                  && best.RunType == RunType.Main
                                                  && best.Stage == 0
                                                  && best.SteamId == steamIdValue)
                            .OrderBy((best, run) => best.BestTime)
                            .OrderBy((best, run) => best.RunId)
                            .Select((best, run) => run)
                            .ToListAsync(OperationCancellation);

        return results;
    }

    private async Task<List<RunEntity>> QueryBestStageRunsForPlayerAsync(SteamID steamId, ulong mapId)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var results = await QueryBestRuns().InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                            .Where((best, run) => best.MapId == mapId
                                                  && best.RunType == RunType.Stage
                                                  && best.SteamId == steamIdValue
                                                  && best.Stage > 0)
                            .OrderBy((best, run) => best.Stage)
                            .OrderBy((best, run) => best.BestTime)
                            .OrderBy((best, run) => best.RunId)
                            .Select((best, run) => run)
                            .ToListAsync(OperationCancellation);

        return results;
    }
}
