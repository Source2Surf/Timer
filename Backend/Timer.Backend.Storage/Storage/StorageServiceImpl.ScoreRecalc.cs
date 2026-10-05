using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Player locks -> score writes -> total update, all in one READ COMMITTED transaction.
    // No map lock, so finishes on the map never wait for a recalculation. Direct calls are a
    // test seam; the worker serializes each board through its Outbox lease.
    internal async Task RecalculateTrackScoresAsync(ulong mapId, int style, ushort track, double styleFactor,
                                                    bool repairTotals = false)
        => _ = await RecalculateTrackScoresCoreAsync(mapId, style, track, styleFactor, repairTotals, null);

    /// <summary>
    /// Runs a recalculation while an optional durable-work fence holds. <paramref name="completeWorkAsync"/>
    /// runs last in the score transaction; when it reports the work superseded (lease lost, or a
    /// wipe or repair queued meanwhile) every score write rolls back.
    /// </summary>
    private async Task<bool> RecalculateTrackScoresCoreAsync(ulong mapId,
                                                              int style,
                                                              ushort track,
                                                              double styleFactor,
                                                              bool repairTotals,
                                                              Func<Task<bool>>? canWriteAsync,
                                                              Func<Task<bool>>? completeWorkAsync = null)
    {
        // Local seed gates must always be acquired before database locks.
        await EnsureBestRunsSeededAsync(mapId, RunType.Main, style, track, 0);
        var recalculated = false;

        try
        {
            await WithRecordTransactionAsync(async () =>
            {
                // WithRecordTransactionAsync may retry after rolling back a lock conflict. Never carry
                // a successful fence result from an earlier attempt into the retry.
                recalculated = false;

                if (canWriteAsync is not null && !await canWriteAsync())
                {
                    return;
                }

                await RecalculateTrackScoresInCurrentTransactionAsync(mapId, style, track, styleFactor, repairTotals);
                if (completeWorkAsync is not null && !await completeWorkAsync()) throw new ScoreRecalcSupersededException();
                recalculated = true;
            });
        }
        catch (ScoreRecalcSupersededException)
        {
            return false;
        }

        return recalculated;
    }

    private sealed class ScoreRecalcSupersededException : Exception;

    private async Task RecalculateTrackScoresInCurrentTransactionAsync(ulong mapId, int style, ushort track, double styleFactor,
                                                                      bool repairTotals)
    {
        // Read current configuration here; queueing never captures a potentially stale tier
        // or score pool, and a tier change queues a repair that supersedes this run.
        var (tier, basePot) = await GetTrackScoreConfigAsync(mapId, track);
        var trackPool = ScoreCalculator.CalculateTrackPool(tier, ScoreCalculator.IsBonus(track), basePot, styleFactor);
        var rankedPlayers = await GetRankedPlayersAsync(mapId, style, track);
        var existing = await GetBoardScoresAsync(mapId, style, track);
        if (rankedPlayers.Count == 0 && existing.Count == 0) return;

        var boardPlayers = repairTotals ? rankedPlayers.Concat(existing.Keys).Distinct().ToList() : null;
        var inserts = new List<PlayerTrackScoreEntity>();
        var updates = new List<PlayerTrackScoreEntity>();
        var now = DateTime.UtcNow;

        // A backend policy factor of zero disables points: no row per ranked player, and
        // every existing row below is removed.
        for (var index = 0; trackPool != 0 && index < rankedPlayers.Count; index++)
        {
            var steamId = rankedPlayers[index];
            var roundedPoints = Math.Round(ScoreCalculator.CalculatePlayerTrackScore(
                trackPool, index + 1, rankedPlayers.Count));
            if (!double.IsFinite(roundedPoints) || roundedPoints < 0 || roundedPoints > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Calculated score for map {mapId}, style {style}, track {track} exceeds the uint points range.");
            }

            var points = (uint)roundedPoints;
            var exists = existing.Remove(steamId, out var previous);
            if (exists && previous.Points == points) continue;
            (exists ? updates : inserts).Add(new PlayerTrackScoreEntity
            {
                Id = exists ? previous.Id : 0, SteamId = steamId, MapId = mapId, Style = style, Track = track,
                Points = points, UpdatedAt = now,
            });
        }

        var removed = existing.Keys.ToList();

        // Only players whose score here changed get new totals. recalc-scores asks for a repair,
        // which re-totals every player on the board and fixes totals older versions left stale.
        var retotal = boardPlayers
                      ?? inserts.Select(x => x.SteamId).Concat(updates.Select(x => x.SteamId)).Concat(removed).ToList();
        if (retotal.Count == 0) return;

        // Acquire all player locks before any score writes. A later SUM statement
        // then sees prior writers' commits even if this transaction had to wait.
        var locked = await LockPlayersForPointsAsync(retotal);

        // The completion fence rolls this run back if another one could have changed these rows
        // since the lookup, so it is authoritative. No Storageable probe, no update per player.
        foreach (var batch in inserts.Chunk(500))
            await _db.Insertable(batch).ExecuteCommandAsync(OperationCancellation);
        foreach (var batch in updates.Chunk(500))
            await _db.Updateable(batch)
                .UpdateColumns(x => new { x.Points, x.UpdatedAt }).ExecuteCommandAsync(OperationCancellation);
        foreach (var batch in removed.Chunk(500))
            await _db.Deleteable<PlayerTrackScoreEntity>()
                .Where(x => x.MapId == mapId && x.Style == style && x.Track == track && batch.Contains(x.SteamId))
                .ExecuteCommandAsync(OperationCancellation);

        await UpdatePlayerTotalPointsAsync(retotal, locked);
    }

    // Locks the players in ascending primary-key order and returns their current totals.
    private async Task<Dictionary<long, LockedPlayerRow>> LockPlayersForPointsAsync(IReadOnlyList<long> steamIds)
    {
        if (_db.Ado.Transaction is null) throw new InvalidOperationException("Player locks require a transaction.");

        var ids = new List<ulong>(steamIds.Count);
        foreach (var batch in steamIds.Chunk(500))
            ids.AddRange(await _db.Queryable<PlayerEntity>().In(x => x.SteamId, batch).Select(x => x.Id).ToListAsync(OperationCancellation));
        ids.Sort();

        // A primary-key-only range avoids secondary-index/filesort lock order differences.
        // Every transaction that locks players takes this ascending order.
        var locked = new Dictionary<long, LockedPlayerRow>(ids.Count);
        foreach (var batch in ids.Chunk(500))
        {
            var query = _db.Queryable<PlayerEntity>().In(x => x.Id, batch).OrderBy(x => x.Id);
            if (_db.CurrentConnectionConfig.DbType != DbType.Sqlite) query = query.TranLock(DbLockType.Wait);
            foreach (var row in await query.Select(x => new LockedPlayerRow
                     {
                         Id = x.Id, SteamId = x.SteamId, Points = SqlFunc.ToInt64(x.Points),
                         JoinedAtUtc = x.JoinedAtUtc, UpdatedAt = x.UpdatedAt,
                     }).ToListAsync(OperationCancellation))
            {
                locked[row.SteamId] = row;
            }
        }

        return locked;
    }

    // Re-totals the locked players from their track scores and writes only the totals that changed.
    private async Task UpdatePlayerTotalPointsAsync(IReadOnlyList<long> idList, Dictionary<long, LockedPlayerRow> locked)
    {
        var now = DateTime.UtcNow;
        var changed = new List<PlayerEntity>();
        var legacy = new List<ulong>();
        var capped = new List<long>();

        // Separate statement AFTER the lock reads; READ COMMITTED refreshes the
        // snapshot here. Atomic SUM alone does not provide this ordering on PG.
        foreach (var batch in idList.Chunk(500))
        {
            // The score tables use uint points, but a player's sum across boards can exceed
            // uint even when every single track score is individually representable. Sum
            // as signed BIGINT before narrowing.
            var totals = (await _db.Queryable<PlayerTrackScoreEntity>()
                                   .In(s => s.SteamId, batch)
                                   .GroupBy(s => s.SteamId)
                                   .Select(s => new PlayerTotalPointsRow
                                   {
                                       SteamId = s.SteamId,
                                       TotalPoints = SqlFunc.AggregateSum(SqlFunc.ToInt64(s.Points)),
                                   })
                                   .ToListAsync(OperationCancellation))
                         .ToDictionary(x => x.SteamId, x => x.TotalPoints);

            foreach (var steamId in batch)
            {
                if (!locked.TryGetValue(steamId, out var player)) continue;

                var total = totals.GetValueOrDefault(steamId);
                if (total < 0) throw new InvalidOperationException("Player total score is negative.");

                // A total above uint is capped rather than failing the transaction: throwing here rolled
                // back the whole board, so one player's cross-board sum stopped every other player's
                // scores on it from updating and eventually dead-lettered the board.
                if (total > uint.MaxValue)
                {
                    capped.Add(steamId);
                    total = uint.MaxValue;
                }

                if (total == player.Points) continue;
                if (player.JoinedAtUtc is null) legacy.Add(player.Id);
                changed.Add(new PlayerEntity { Id = player.Id, SteamId = steamId, Points = (uint)total, UpdatedAt = now });
            }
        }

        if (capped.Count > 0)
        {
            _logger.LogWarning(
                "Capping the total score of {Count} player(s) at {Max}; their per-board scores sum past the uint range. SteamIds: {SteamIds}",
                capped.Count, uint.MaxValue, string.Join(", ", capped.Take(20)));
        }

        // Rows from older writers: freeze the join date from UpdatedAt in SQL before the batch
        // below changes UpdatedAt. Batches write values as literals, so join dates stay out of them.
        foreach (var batch in legacy.Chunk(500))
            await _db.Updateable<PlayerEntity>()
                     .SetColumns(p => p.JoinedAtUtc == SqlFunc.IsNull(p.JoinedAtUtc, p.UpdatedAt))
                     .Where(p => batch.Contains(p.Id))
                     .ExecuteCommandAsync(OperationCancellation);

        foreach (var batch in changed.Chunk(500))
            await _db.Updateable(batch.ToList())
                     .UpdateColumns(x => new { x.Points, x.UpdatedAt })
                     .ExecuteCommandAsync(OperationCancellation);
    }

    private sealed class LockedPlayerRow
    {
        public ulong     Id          { get; set; }
        [SugarColumn(ColumnDataType = "bigint")]
        public long      SteamId     { get; set; }
        public long      Points      { get; set; }
        public DateTime? JoinedAtUtc { get; set; }
        public DateTime  UpdatedAt   { get; set; }
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

    // The board's players, fastest first (ties by the earlier run).
    private async Task<List<long>> GetRankedPlayersAsync(ulong mapId, int style, ushort track)
    {
        var read = CachedShape("board-ranking", () =>
        {
            var map = Sentinel.MapId;
            var styleArg = Sentinel.Style;
            var trackArg = Sentinel.Track;
            const ushort stage = 0;

            return QueryBestRuns().Where(r => r.MapId == map && r.RunType == RunType.Main && r.Style == styleArg
                                              && r.Track == trackArg && r.Stage == stage)
                                  .OrderBy(r => r.BestTime)
                                  .OrderBy(r => r.RunId)
                                  .Select(r => r.SteamId)
                                  .ToSql();
        }, Sentinel.MapId, Sentinel.Style, Sentinel.Track);

        await using var reader = await ReadAsync(read, mapId, style, track);
        var players = new List<long>();

        while (await reader.ReadAsync(OperationCancellation))
        {
            players.Add(reader.GetInt64(0));
        }

        return players;
    }

    private async Task<Dictionary<long, (ulong Id, uint Points)>> GetBoardScoresAsync(ulong mapId, int style, ushort track)
    {
        var read = CachedShape("board-scores", () =>
        {
            var map = Sentinel.MapId;
            var styleArg = Sentinel.Style;
            var trackArg = Sentinel.Track;

            return _db.Queryable<PlayerTrackScoreEntity>()
                      .Where(x => x.MapId == map && x.Style == styleArg && x.Track == trackArg)
                      .Select(x => new ExistingTrackScoreRow { Id = x.Id, SteamId = x.SteamId, Points = x.Points })
                      .ToSql();
        }, Sentinel.MapId, Sentinel.Style, Sentinel.Track);

        await using var reader = await ReadAsync(read, mapId, style, track);
        var o = read.Ordinals(reader, BoardScoreColumns);
        var scores = new Dictionary<long, (ulong Id, uint Points)>();

        while (await reader.ReadAsync(OperationCancellation))
        {
            scores[reader.GetInt64(o[1])] = (unchecked((ulong)reader.GetInt64(o[0])), (uint)reader.GetInt64(o[2]));
        }

        return scores;
    }

    private static readonly string[] BoardScoreColumns =
        [nameof(ExistingTrackScoreRow.Id), nameof(ExistingTrackScoreRow.SteamId), nameof(ExistingTrackScoreRow.Points)];

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
                    resolvedMapId, combo.Style, combo.Track, styleFactor, now, repairTotals: true);
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
