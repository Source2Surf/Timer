using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    // Map lock -> player locks -> score writes -> total update, all in one
    // READ COMMITTED transaction. Different maps can proceed concurrently.
    internal async Task RecalculateTrackScoresAsync(ulong mapId, int style, ushort track, double styleFactor)
    {
        // Local seed gates must always be acquired before database locks.
        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, 0);
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            // Read current configuration while the map is locked; queueing never
            // captures a potentially stale tier or score pool.
            var (tier, basePot) = await GetTrackScoreConfigAsync(mapId, track);
            var trackPool = ScoreCalculator.CalculateTrackPool(tier, ScoreCalculator.IsBonus(track), basePot, styleFactor);
            var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track);
            var existingScores = await _db.Queryable<PlayerTrackScoreEntity>()
                .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
                .Select(x => new ExistingTrackScoreRow { Id = x.Id, SteamId = x.SteamId, Points = x.Points })
                .ToListAsync();
            var existing = existingScores.ToDictionary(x => x.SteamId);
            var affected = rankedPlayers.Select(x => x.SteamId).Concat(existing.Keys).Distinct().ToList();
            if (affected.Count == 0) return;

            // Acquire all player locks before any score writes. A later SUM statement
            // then sees prior writers' commits even if this transaction had to wait.
            await LockPlayersForPointsAsync(affected);
            var inserts = new List<PlayerTrackScoreEntity>();
            var updates = new List<PlayerTrackScoreEntity>();
            var now = DateTime.UtcNow;
            for (var index = 0; index < rankedPlayers.Count; index++)
            {
                var steamId = rankedPlayers[index].SteamId;
                var points = (uint)Math.Round(ScoreCalculator.CalculatePlayerTrackScore(trackPool, index + 1, rankedPlayers.Count));
                var exists = existing.Remove(steamId, out var previous);
                if (exists && previous!.Points == points) continue;
                (exists ? updates : inserts).Add(new PlayerTrackScoreEntity
                {
                    Id = previous?.Id ?? 0, SteamId = steamId, MapId = mapId, Style = style, Track = track,
                    Points = points, UpdatedAt = now,
                });
            }

            // The map lock makes our existing-row lookup authoritative. No second
            // Storageable existence probe and no individual update per player.
            foreach (var batch in inserts.Chunk(500))
                await _db.Insertable(batch).ExecuteCommandAsync();
            foreach (var batch in updates.Chunk(500))
                await _db.Updateable(batch)
                    .UpdateColumns(x => new { x.Points, x.UpdatedAt }).ExecuteCommandAsync();
            foreach (var batch in existing.Keys.Chunk(500))
                await _db.Deleteable<PlayerTrackScoreEntity>()
                    .Where(x => x.MapId == mapId && x.Style == style && x.Track == track && batch.Contains(x.SteamId))
                    .ExecuteCommandAsync();

            // Also repairs totals left stale by older versions. Unchanged totals
            // are filtered in SQL so they do not generate writes or change UpdatedAt.
            await UpdatePlayerTotalPointsAsync(affected);
        });
    }

    private async Task LockPlayersForPointsAsync(IReadOnlyList<long> steamIds)
    {
        if (_db.Ado.Transaction is null) throw new InvalidOperationException("Player locks require a transaction.");
        if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite) return;

        var ids = new List<ulong>(steamIds.Count);
        foreach (var batch in steamIds.Chunk(500))
            ids.AddRange(await _db.Queryable<PlayerEntity>().Where(x => batch.Contains(x.SteamId)).Select(x => x.Id).ToListAsync());
        ids.Sort();
        // A primary-key-only projection/range avoids secondary-index/filesort lock
        // order differences. All map transactions take the same ascending order.
        foreach (var batch in ids.Chunk(500))
            await _db.Queryable<PlayerEntity>().Where(x => batch.Contains(x.Id)).OrderBy(x => x.Id)
                .TranLock(DbLockType.Wait).Select(x => x.Id).ToListAsync();
    }

    private async Task UpdatePlayerTotalPointsAsync(IReadOnlyList<long> idList)
    {
        var now = DateTime.UtcNow;
        // Separate statement AFTER the lock reads; READ COMMITTED refreshes the
        // snapshot here. Atomic SUM alone does not provide this ordering on PG.
        foreach (var batch in idList.Chunk(500))
            await _db.Updateable<PlayerEntity>()
                .SetColumns(p => p.Points == SqlFunc.IsNull(SqlFunc.Subqueryable<PlayerTrackScoreEntity>()
                    .Where(s => s.SteamId == p.SteamId).Sum(s => s.Points), 0u))
                .SetColumns(p => p.UpdatedAt == now)
                .Where(p => batch.Contains(p.SteamId) && p.Points != SqlFunc.IsNull(SqlFunc.Subqueryable<PlayerTrackScoreEntity>()
                    .Where(s => s.SteamId == p.SteamId).Sum(s => s.Points), 0u))
                .ExecuteCommandAsync();
    }

    /// <summary>
    /// Get the score configuration (Tier and BasePot) for a given track.
    /// </summary>
    private async Task<(int Tier, int BasePot)> GetTrackScoreConfigAsync(ulong mapId, ushort track)
    {
        var row = await _db.Queryable<MapEntity>()
                           .LeftJoin<MapTrackEntity>((map, trackTier) => map.MapId == trackTier.MapId
                                                                          && trackTier.Track == track)
                           .Where(map => map.MapId == mapId)
                           .Select((map, trackTier) => new
                           {
                               map.Tier,
                               map.BasePot,
                               TrackTier = trackTier.Tier,
                           })
                           .FirstAsync();

        var config = ((int)(track == 0 ? row?.Tier ?? 1 : row?.TrackTier ?? 1),
                      (int)(row?.BasePot ?? 0));

        return config;
    }

    /// <summary>
    /// Get the ranked player list for a given track (sorted by time, best per player).
    /// </summary>
    private async Task<List<RankedPlayerRow>> GetRankedPlayersAsync(ulong mapId, int style, ushort track)
    {
        const ushort stage = 0;

        var players = await QueryBestRuns()
            .Where(r => r.MapId == mapId
                        && r.RunType == RunType.Main
                        && r.Style == style
                        && r.Track == track
                        && r.Stage == stage)
            .OrderBy(r => r.BestTime)
            .OrderBy(r => r.RunId)
            .Select(r => new RankedPlayerRow
            {
                SteamId = r.SteamId,
                BestTime = r.BestTime,
            })
            .ToListAsync();

        return players;
    }

    private sealed class RankedPlayerRow
    {
        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }
        public float BestTime { get; set; }
    }

    private sealed class ExistingTrackScoreRow
    {
        public ulong Id { get; set; }

        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        public uint Points { get; set; }
    }

    /// <summary>
    /// Manually trigger score recalculation for all tracks on a given map.
    /// </summary>
    public async Task<int> RecalculateMapScoresAsync(string mapName, IReadOnlyDictionary<int, double>? styleFactors = null)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);
        if (mapId is null)
        {
            return 0;
        }

        var trackCombinations = await _db.Queryable<RunEntity>()
            .Where(run => run.MapId == mapId.Value && run.RunType == RunType.Main && run.Stage == 0)
            .GroupBy(run => new { run.Style, run.Track })
            .Select(run => new RecalcTrackCombinationRow { Style = run.Style, Track = run.Track })
            .ToListAsync();

        // Enqueue a recalculation request for each (style, track) combination
        foreach (var combo in trackCombinations)
        {
            // Look up the style factor from the dictionary; default to 1.0 if not found
            var styleFactor = styleFactors?.TryGetValue(combo.Style, out var factor) == true ? factor : 1.0;

            _scoreRecalcScheduler.Enqueue(new RecalcRequest(mapId.Value, combo.Style, combo.Track, styleFactor));
        }

        return trackCombinations.Count;
    }

    private sealed class RecalcTrackCombinationRow
    {
        public int Style { get; set; }

        public ushort Track { get; set; }
    }
}
