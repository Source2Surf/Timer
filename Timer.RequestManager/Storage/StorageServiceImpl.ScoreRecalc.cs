using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    // Map lock -> player locks -> score writes -> total update, all in one
    // READ COMMITTED transaction. Different maps can proceed concurrently.
    internal async Task RecalculateTrackScoresAsync(ulong mapId, int style, ushort track, double styleFactor)
        => _ = await RecalculateTrackScoresCoreAsync(mapId, style, track, styleFactor, null);

    /// <summary>
    /// Runs a recalculation only while an optional durable-work fence still holds. The fence is
    /// evaluated after taking the map lock and before reading or writing score state, so a worker
    /// holding an older generation cannot overwrite newer scores with stale parameters.
    /// </summary>
    private async Task<bool> RecalculateTrackScoresCoreAsync(ulong mapId,
                                                              int style,
                                                              ushort track,
                                                              double styleFactor,
                                                              Func<Task<bool>>? canWriteAsync,
                                                              Func<Task>? completeWorkAsync = null)
    {
        // Local seed gates must always be acquired before database locks.
        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, 0);
        var recalculated = false;
        await WithRecordTransactionAsync(async () =>
        {
            // WithRecordTransactionAsync may retry after rolling back a lock conflict. Never carry
            // a successful fence result from an earlier attempt into the retry.
            recalculated = false;
            await LockMapAsync(mapId);

            if (canWriteAsync is not null && !await canWriteAsync())
            {
                return;
            }

            recalculated = true;
            await RecalculateTrackScoresInCurrentTransactionAsync(mapId, style, track, styleFactor);
            if (completeWorkAsync is not null) await completeWorkAsync();
        });

        return recalculated;
    }

    private async Task RecalculateTrackScoresInCurrentTransactionAsync(ulong mapId, int style, ushort track, double styleFactor)
    {
        // Read current configuration while the map is locked; queueing never
        // captures a potentially stale tier or score pool.
        var (tier, basePot) = await GetTrackScoreConfigAsync(mapId, track);
        var trackPool = ScoreCalculator.CalculateTrackPool(tier, ScoreCalculator.IsBonus(track), basePot, styleFactor);
        var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track);
        var existingScores = await _db.Queryable<PlayerTrackScoreEntity>()
            .Where(x => x.MapId == mapId && x.Style == style && x.Track == track)
            .Select(x => new ExistingTrackScoreRow { Id = x.Id, SteamId = x.SteamId, Points = x.Points })
            .ToListAsync(OperationCancellation);
        var existing = existingScores.ToDictionary(x => x.SteamId);
        var affected = rankedPlayers.Select(x => x.SteamId).Concat(existing.Keys).Distinct().ToList();
        if (affected.Count == 0) return;

        // Acquire all player locks before any score writes. A later SUM statement
        // then sees prior writers' commits even if this transaction had to wait.
        await LockPlayersForPointsAsync(affected);
        if (trackPool == 0)
        {
            // A backend policy factor of zero disables points. Do not create one
            // zero-point row per ranked player, and remove any points from a prior
            // nonzero policy before repairing player totals.
            foreach (var batch in existing.Keys.Chunk(500))
                await _db.Deleteable<PlayerTrackScoreEntity>()
                    .Where(x => x.MapId == mapId && x.Style == style && x.Track == track && batch.Contains(x.SteamId))
                    .ExecuteCommandAsync(OperationCancellation);
            await UpdatePlayerTotalPointsAsync(affected);
            return;
        }

        var inserts = new List<PlayerTrackScoreEntity>();
        var updates = new List<PlayerTrackScoreEntity>();
        var now = DateTime.UtcNow;
        for (var index = 0; index < rankedPlayers.Count; index++)
        {
            var steamId = rankedPlayers[index].SteamId;
            var roundedPoints = Math.Round(ScoreCalculator.CalculatePlayerTrackScore(
                trackPool, index + 1, rankedPlayers.Count));
            if (!double.IsFinite(roundedPoints) || roundedPoints < 0 || roundedPoints > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Calculated score for map {mapId}, style {style}, track {track} exceeds the uint points range.");
            }

            var points = (uint)roundedPoints;
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
            await _db.Insertable(batch).ExecuteCommandAsync(OperationCancellation);
        foreach (var batch in updates.Chunk(500))
            await _db.Updateable(batch)
                .UpdateColumns(x => new { x.Points, x.UpdatedAt }).ExecuteCommandAsync(OperationCancellation);
        foreach (var batch in existing.Keys.Chunk(500))
            await _db.Deleteable<PlayerTrackScoreEntity>()
                .Where(x => x.MapId == mapId && x.Style == style && x.Track == track && batch.Contains(x.SteamId))
                .ExecuteCommandAsync(OperationCancellation);

        // Also repairs totals left stale by older versions. Unchanged totals
        // are filtered in SQL so they do not generate writes or change UpdatedAt.
        await UpdatePlayerTotalPointsAsync(affected);
    }

    private async Task LockPlayersForPointsAsync(IReadOnlyList<long> steamIds)
    {
        if (_db.Ado.Transaction is null) throw new InvalidOperationException("Player locks require a transaction.");
        if (_db.CurrentConnectionConfig.DbType == DbType.Sqlite) return;

        var ids = new List<ulong>(steamIds.Count);
        foreach (var batch in steamIds.Chunk(500))
            ids.AddRange(await _db.Queryable<PlayerEntity>().Where(x => batch.Contains(x.SteamId)).Select(x => x.Id).ToListAsync(OperationCancellation));
        ids.Sort();
        // A primary-key-only projection/range avoids secondary-index/filesort lock
        // order differences. All map transactions take the same ascending order.
        foreach (var batch in ids.Chunk(500))
            await _db.Queryable<PlayerEntity>().Where(x => batch.Contains(x.Id)).OrderBy(x => x.Id)
                .TranLock(DbLockType.Wait).Select(x => x.Id).ToListAsync(OperationCancellation);
    }

    private async Task UpdatePlayerTotalPointsAsync(IReadOnlyList<long> idList)
    {
        var now = DateTime.UtcNow;
        // Separate statement AFTER the lock reads; READ COMMITTED refreshes the
        // snapshot here. Atomic SUM alone does not provide this ordering on PG.
        foreach (var batch in idList.Chunk(500))
        {
            // The score tables use uint points, but a player's sum across boards can exceed
            // uint even when every single track score is individually representable. Check
            // in SQL as signed BIGINT before the provider coerces SUM into PlayerEntity.Points.
            var totals = await _db.Queryable<PlayerTrackScoreEntity>()
                                  .Where(s => batch.Contains(s.SteamId))
                                  .GroupBy(s => s.SteamId)
                                  .Select(s => new PlayerTotalPointsRow
                                  {
                                      SteamId = s.SteamId,
                                      TotalPoints = SqlFunc.AggregateSum(SqlFunc.ToInt64(s.Points)),
                                  })
                                  .ToListAsync(OperationCancellation);
            if (totals.Any(x => x.TotalPoints < 0 || x.TotalPoints > uint.MaxValue))
            {
                throw new InvalidOperationException("Player total score exceeds the uint points range.");
            }

            await _db.Updateable<PlayerEntity>()
                .SetColumns(p => p.Points == SqlFunc.IsNull(SqlFunc.Subqueryable<PlayerTrackScoreEntity>()
                    .Where(s => s.SteamId == p.SteamId).Sum(s => s.Points), 0u))
                // Freeze legacy/null join dates before UpdatedAt changes (also preserves
                // MySQL's left-to-right single-table assignment semantics).
                .SetColumns(p => p.JoinedAtUtc == SqlFunc.IsNull(p.JoinedAtUtc, p.UpdatedAt))
                .SetColumns(p => p.UpdatedAt == now)
                .Where(p => batch.Contains(p.SteamId) && p.Points != SqlFunc.IsNull(SqlFunc.Subqueryable<PlayerTrackScoreEntity>()
                    .Where(s => s.SteamId == p.SteamId).Sum(s => s.Points), 0u))
                .ExecuteCommandAsync(OperationCancellation);
        }
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
                           .FirstAsync(OperationCancellation);

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
            .ToListAsync(OperationCancellation);

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

    private sealed class PlayerTotalPointsRow
    {
        [SugarColumn(ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        public long TotalPoints { get; set; }
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
            .ToListAsync(OperationCancellation);

        if (trackCombinations.Count == 0)
        {
            return 0;
        }

        var resolvedMapId = mapId.Value;
        var now = DateTime.UtcNow;
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(resolvedMapId);

            // One map lock and one transaction merge every requested board. This avoids a lock
            // round-trip per combo while preserving the same atomic enqueue guarantee as PB/WR.
            foreach (var combo in trackCombinations)
            {
                // Look up the style factor from the dictionary; default to 1.0 if not found.
                var styleFactor = styleFactors?.TryGetValue(combo.Style, out var factor) == true ? factor : 1.0;
                await EnqueueScoreRecalcInCurrentRecordTransactionAsync(
                    resolvedMapId, combo.Style, combo.Track, styleFactor, now);
            }
        });

        WakeScoreRecalcWorker();

        return trackCombinations.Count;
    }

    private sealed class RecalcTrackCombinationRow
    {
        public int Style { get; set; }

        public ushort Track { get; set; }
    }
}
