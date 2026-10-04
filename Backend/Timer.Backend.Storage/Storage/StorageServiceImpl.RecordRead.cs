using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        return await ReadPlayerBestsAsync(mapId.Value, ToDbSteamId(steamId), false, IRequestManager.DefaultRecordLimit);
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

        return await ReadPlayerBestsAsync(mapId.Value, ToDbSteamId(steamId), true, IRequestManager.DefaultRecordLimit);
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

        return await ReadPlayerBestsAsync(mapId.Value, unchecked((long)steamId), stageRecords, NormalizeLimit(limit));
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
        // One statement, so the player's points and the players ahead come from the same snapshot.
        // The ahead count runs on the points index and reads only those players.
        var read = CachedShape("player-rank", () =>
        {
            var steamId = Sentinel.SteamId;

            return _db.Queryable<PlayerEntity>()
                      .Where(me => me.SteamId == steamId)
                      .Select(me => new PlayerRankRow
                      {
                          Points = me.Points,
                          Ahead  = SqlFunc.Subqueryable<PlayerEntity>().Where(other => other.Points > me.Points).Count(),
                      })
                      .Take(1)
                      .ToSql();
        }, Sentinel.SteamId);

        long points, ahead;

        await using (var reader = await ReadAsync(read, steamIdValue))
        {
            if (!await reader.ReadAsync(OperationCancellation))
            {
                return (0, 0);
            }

            var o = read.Ordinals(reader, PlayerRankColumns);
            points = reader.GetInt64(o[0]);
            ahead  = reader.GetInt64(o[1]);
        }

        // No row and zero points both mean unranked.
        if (points == 0)
        {
            return (0, 0);
        }

        // The cached total can be a few seconds old; never report a rank past it ("rank 6 of 5").
        var rank = ahead + 1;

        return ((int)rank, (int)Math.Max(rank, await GetRankedPlayerCountAsync()));
    }

    // Only changes when a recalculation gives a player their first points or takes their last,
    // while counting it reads every ranked player.
    private static readonly TimeSpan RankedPlayerCountLifetime = TimeSpan.FromSeconds(10);

    private sealed record RankedPlayerCount(long Count, long ExpiresAtMs);

    private static readonly string[] PlayerRankColumns = [nameof(PlayerRankRow.Points), nameof(PlayerRankRow.Ahead)];

    // The projection the rank read's SQL is generated from; never materialized.
    private sealed class PlayerRankRow
    {
        public uint Points { get; set; }
        public int  Ahead  { get; set; }
    }

    private RankedPlayerCount? _rankedPlayerCount;

    private async Task<long> GetRankedPlayerCountAsync()
    {
        var now = Environment.TickCount64;

        if (Volatile.Read(ref _rankedPlayerCount) is { } cached && cached.ExpiresAtMs > now)
        {
            return cached.Count;
        }

        var read = CachedShape("ranked-player-count",
                               () => _db.Queryable<PlayerEntity>().Where(x => x.Points > 0).Select(x => SqlFunc.AggregateCount(x.Id)).ToSql());

        await using var reader = await ReadAsync(read);
        var count = await reader.ReadAsync(OperationCancellation) ? reader.GetInt64(0) : 0;
        Volatile.Write(ref _rankedPlayerCount, new RankedPlayerCount(count, now + (long)RankedPlayerCountLifetime.TotalMilliseconds));

        return count;
    }

    public async Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId)
    {
        var read = CachedShape("record-checkpoints", () =>
        {
            var run = Sentinel.RunId;

            return _db.Queryable<RunSegmentEntity>()
                      .Where(s => s.RunId == run)
                      .OrderBy(s => s.Stage)
                      .Select(s => new CheckpointRow
                      {
                          Id             = SqlFunc.ToInt64(s.Id),
                          Stage          = SqlFunc.ToInt32(s.Stage),
                          Time           = s.Time,
                          Sync           = s.Sync,
                          VelocityStartX = s.VelocityStartX,
                          VelocityStartY = s.VelocityStartY,
                          VelocityStartZ = s.VelocityStartZ,
                          VelocityEndX   = s.VelocityEndX,
                          VelocityEndY   = s.VelocityEndY,
                          VelocityEndZ   = s.VelocityEndZ,
                          VelocityMaxX   = s.VelocityMaxX,
                          VelocityMaxY   = s.VelocityMaxY,
                          VelocityMaxZ   = s.VelocityMaxZ,
                          VelocityAvgX   = s.VelocityAvgX,
                          VelocityAvgY   = s.VelocityAvgY,
                          VelocityAvgZ   = s.VelocityAvgZ,
                      })
                      .ToSql();
        }, Sentinel.RunId);

        await using var reader = await ReadAsync(read, (ulong)recordId);
        var o      = read.Ordinals(reader, CheckpointColumns);
        var result = new List<RunCheckpoint>();

        while (await reader.ReadAsync(OperationCancellation))
        {
            result.Add(new RunCheckpoint
            {
                Id              = reader.GetInt64(o[0]),
                RecordId        = recordId,
                CheckpointIndex = (uint)reader.GetInt64(o[1]),
                Time            = reader.GetFloat(o[2]),
                Sync            = reader.GetFloat(o[3]),
                VelocityStartX  = reader.GetFloat(o[4]),
                VelocityStartY  = reader.GetFloat(o[5]),
                VelocityStartZ  = reader.GetFloat(o[6]),
                VelocityEndX    = reader.GetFloat(o[7]),
                VelocityEndY    = reader.GetFloat(o[8]),
                VelocityEndZ    = reader.GetFloat(o[9]),
                VelocityMaxX    = reader.GetFloat(o[10]),
                VelocityMaxY    = reader.GetFloat(o[11]),
                VelocityMaxZ    = reader.GetFloat(o[12]),
                VelocityAvgX    = reader.GetFloat(o[13]),
                VelocityAvgY    = reader.GetFloat(o[14]),
                VelocityAvgZ    = reader.GetFloat(o[15]),
            });
        }

        return result;
    }

    private static readonly string[] CheckpointColumns =
    [
        nameof(CheckpointRow.Id), nameof(CheckpointRow.Stage), nameof(CheckpointRow.Time), nameof(CheckpointRow.Sync),
        nameof(CheckpointRow.VelocityStartX), nameof(CheckpointRow.VelocityStartY), nameof(CheckpointRow.VelocityStartZ),
        nameof(CheckpointRow.VelocityEndX), nameof(CheckpointRow.VelocityEndY), nameof(CheckpointRow.VelocityEndZ),
        nameof(CheckpointRow.VelocityMaxX), nameof(CheckpointRow.VelocityMaxY), nameof(CheckpointRow.VelocityMaxZ),
        nameof(CheckpointRow.VelocityAvgX), nameof(CheckpointRow.VelocityAvgY), nameof(CheckpointRow.VelocityAvgZ),
    ];

    // The projection the checkpoint read's SQL is generated from; never materialized.
    private sealed class CheckpointRow
    {
        public long  Id             { get; set; }
        public int   Stage          { get; set; }
        public float Time           { get; set; }
        public float Sync           { get; set; }
        public float VelocityStartX { get; set; }
        public float VelocityStartY { get; set; }
        public float VelocityStartZ { get; set; }
        public float VelocityEndX   { get; set; }
        public float VelocityEndY   { get; set; }
        public float VelocityEndZ   { get; set; }
        public float VelocityMaxX   { get; set; }
        public float VelocityMaxY   { get; set; }
        public float VelocityMaxZ   { get; set; }
        public float VelocityAvgX   { get; set; }
        public float VelocityAvgY   { get; set; }
        public float VelocityAvgZ   { get; set; }
    }

    // A player's best runs on the map's main or stage boards, with their name.
    private Task<IReadOnlyList<RunRecord>> ReadPlayerBestsAsync(ulong mapId, long steamId, bool stageRecords, int limit)
    {
        var read = CachedShape($"player-bests:{stageRecords}:{limit}", () =>
        {
            var map        = Sentinel.MapId;
            var playerId   = Sentinel.SteamId;
            var query      = QueryBoard().Where((best, run, player) => best.MapId == map && best.SteamId == playerId);

            query = stageRecords
                        ? query.Where((best, run, player) => best.RunType == RunType.Stage && best.Stage > 0)
                               .OrderBy((best, run, player) => best.Stage)
                               .OrderBy((best, run, player) => best.BestTime)
                               .OrderBy((best, run, player) => best.RunId)
                        : query.Where((best, run, player) => best.RunType == RunType.Main && best.Stage == 0)
                               .OrderBy((best, run, player) => best.BestTime)
                               .OrderBy((best, run, player) => best.RunId);

            return SelectBoard(query).Take(limit).ToSql();
        }, Sentinel.MapId, Sentinel.SteamId);

        return ReadRunRecordsAsync(read, mapId, steamId);
    }
}
