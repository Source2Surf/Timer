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

namespace Timer.RequestManager.Storage;

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
                                          .FirstAsync();

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

    public async Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var now = DateTime.UtcNow;

        var player = await _db.Queryable<PlayerEntity>()
                              .Where(x => x.SteamId == steamIdValue)
                              .FirstAsync();

        if (player is null)
        {
            player = new ()
            {
                SteamId   = ToDbSteamId(steamId),
                Name      = name,
                Points    = 0,
                Runs      = 0,
                UpdatedAt = now,
            };

            try
            {
                player.Id = checked((ulong)await _db.Insertable(player).ExecuteReturnBigIdentityAsync());
            }
            catch (Exception ex) when (IsUniqueKeyViolation(ex))
            {
                // Another server may create the profile after our first read.
                // This insert is in autocommit, so PG can safely read the winner.
                player = await _db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamIdValue).FirstAsync();
                if (player is null) throw;
            }
        }

        // Only write back if name changed
        if (player.Name != name)
        {
            player.Name      = name;
            player.UpdatedAt = now;

            await _db.Updateable(player)
                     .UpdateColumns(x => new { x.Name, x.UpdatedAt })
                     .ExecuteCommandAsync();
        }

        var profile = new PlayerProfile
        {
            Id           = (long) player.Id,
            SteamId      = steamId,
            Points       = player.Points,
            JoinDate     = player.UpdatedAt, // first record's UpdatedAt serves as join date
            LastSeenDate = now,
        };

        profile.UpdateName(name);

        return profile;
    }

    public async Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
    {
        var steamIdValue = ToDbSteamId(steamId);
        // Scalar projection (Points is a plain uint — no SteamID-converter concern);
        // no row and zero points both come back as 0.
        var playerPoints = await _db.Queryable<PlayerEntity>()
                                    .Where(x => x.SteamId == steamIdValue)
                                    .Select(x => x.Points)
                                    .FirstAsync();

        if (playerPoints == 0)
        {
            return (0, 0);
        }

        // Single query: COUNT(*) for total, SUM(CASE) for rank
        var stats = await _db.Queryable<PlayerEntity>()
                             .Where(x => x.Points > 0)
                             .Select(_ => new
                             {
                                 Total = SqlFunc.AggregateCount(_.Id),
                                 Ahead = SqlFunc.AggregateSum(SqlFunc.IIF(_.Points > playerPoints, 1, 0)),
                             })
                             .FirstAsync();

        if (stats is null)
        {
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
                                .ToListAsync();

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
                            .ToListAsync();

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
                            .ToListAsync();

        return results;
    }
}
