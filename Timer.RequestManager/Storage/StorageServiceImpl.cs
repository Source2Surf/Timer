using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Scheduling;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl : IRequestManager
{
    private readonly SqlSugarScope               _db;
    private readonly ILogger<StorageServiceImpl> _logger;
    private readonly ConcurrentDictionary<string, ulong> _mapIdCache = new (StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), byte> _bestRunMapSeededCache = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), SemaphoreSlim> _bestRunSeedLocks = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType, int style, ushort track, ushort stage), byte> _bestRunSeededCache = new();
    private readonly ScoreRecalcScheduler        _scoreRecalcScheduler;

    internal SqlSugarScope Db => _db;

    public StorageServiceImpl(DbType dbType, string connectionString, ILogger<StorageServiceImpl> logger)
    {
        _db     = CreateClient(dbType, connectionString);
        _logger = logger;
        _scoreRecalcScheduler = new ScoreRecalcScheduler(HandleScoreRecalcAsync, logger);
    }

    private async Task HandleScoreRecalcAsync(RecalcRequest request)
    {
        // A pass is transactional. Retry temporary failures from background work;
        // best-run and score writes are idempotent across complete attempts.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await RecalculateTrackScoresAsync(request.MapId, request.Style, request.Track, request.StyleFactor);
                return;
            }
            catch (Exception ex) when (attempt < 3)
            {
                _logger.LogWarning(ex, "Score recalc attempt {Attempt} failed for map {MapId}; retrying.", attempt, request.MapId);
                await Task.Delay(TimeSpan.FromSeconds(attempt));
            }
        }
    }

    public void Init()
    {
        _mapIdCache.Clear();
        _bestRunMapSeededCache.Clear();
        _bestRunSeededCache.Clear();

        MigrateReplaySteamIdColumn();

        try
        {
            _db.CodeFirst.InitTables(typeof(MapEntity),
                                     typeof(MapTrackEntity),
                                     typeof(PlayerEntity),
                                     typeof(PlayerMapStatsEntity),
                                     typeof(PlayerBestRunEntity),
                                     typeof(PlayerTrackScoreEntity),
                                     typeof(RunEntity),
                                     typeof(RunSegmentEntity),
                                     typeof(ReplayEntity),
                                     typeof(ZoneEntity));
        }
        catch (Exception e)
        {
            // Propagate: a failed InitTables means the DB is unreachable/broken. Swallowing
            // it would report Init success, register this backend, and displace the working
            // LiteDB fallback. Table creation must not report success after a failure.
            _logger.LogError(e, "Error when initializing tables");

            throw;
        }

        EnsureTrackScoreCoveringIndex();
    }

    /// <summary>
    /// Ensures the covering index that serves the score-recalc delta read
    /// (WHERE MapId=? AND Style=? AND Track=?, no SteamId predicate) exists. SqlSugar's
    /// <c>InitTables</c> creates declared indexes only when it first creates the table, so an existing
    /// production table would otherwise keep full-scanning the scores table on every recalc.
    /// </summary>
    private void EnsureTrackScoreCoveringIndex()
    {
        const string tableName = "surf_player_track_scores";
        const string indexName = "idx_player_track_scores_map_style_track";

        try
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                return;
            }

            if (_db.DbMaintenance.IsAnyIndex(indexName))
            {
                return;
            }

            _db.DbMaintenance.CreateIndex(tableName,
                [nameof(PlayerTrackScoreEntity.MapId), nameof(PlayerTrackScoreEntity.Style), nameof(PlayerTrackScoreEntity.Track),
                 nameof(PlayerTrackScoreEntity.SteamId), nameof(PlayerTrackScoreEntity.Points)], indexName, false);
            _logger.LogInformation("Created covering index {Index} on {Table}", indexName, tableName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to ensure covering index {Index} on {Table}", indexName, tableName);
        }
    }

    public void Shutdown()
    {
        _scoreRecalcScheduler.Dispose();
        _db.Dispose();
    }

    public async Task<MapProfile> GetMapInfo(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await EnsureMapEntityByKeyAsync(mapKey, map);

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync();

        return ToMapProfile(mapInfo, trackTiers);
    }

    public async Task UpdateMapInfo(MapProfile info)
    {
        var mapKey = ToMapKey(info.MapName);

        var mapId = await EnsureMapIdByNameAsync(info.MapName);
        var tier = GetTier(info.Tier, 0);
        var stages = ToUInt16(info.Stages);
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            // Update only fields supplied by the map profile. In particular do not
            // copy a stale BasePot over an administrator's concurrent change.
            await _db.Updateable<MapEntity>()
                .SetColumns(x => x.File == mapKey)
                .SetColumns(x => x.Tier == tier)
                .SetColumns(x => x.Stages == stages)
                .SetColumns(x => x.Bonuses == info.Bonuses)
                .SetColumns(x => x.PlayCount == info.PlayCount)
                .SetColumns(x => x.TotalPlayTime == info.TotalPlayTime)
                .Where(x => x.MapId == mapId).ExecuteCommandAsync();
            await SyncMapTrackTiersAsync(mapId, info.Tier);
        });

        // Cache writes only after a successful commit, so a rollback can never leave
        // the caches holding state from a transaction that never happened.
        _mapIdCache[mapKey] = mapId;
    }

}
