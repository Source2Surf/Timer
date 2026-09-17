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
    private readonly SqlSugarScope               _rootDb;
    private ISqlSugarClient _db => _operationDb.Value ?? (ISqlSugarClient)_rootDb;
    private readonly ILogger<StorageServiceImpl> _logger;
    private readonly ConcurrentDictionary<string, ulong> _mapIdCache = new (StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), byte> _bestRunMapSeededCache = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType), SemaphoreSlim> _bestRunSeedLocks = new();
    private readonly ConcurrentDictionary<(ulong mapId, RunType runType, int style, ushort track, ushort stage), byte> _bestRunSeededCache = new();
    private readonly StorageServiceImpl?          _scoreRecalcWorkerStorage;
    private readonly ScoreRecalcScheduler?        _scoreRecalcScheduler;
    private int _workerStopRequested;
    private int _shutdown;
    private DateTime? _workerStartedAtUtc;

    internal bool HasScoreRecalcWorker => _scoreRecalcScheduler is not null;

    internal SqlSugarScope Db => _rootDb;

    public StorageServiceImpl(DbType                      dbType,
                              string                      connectionString,
                              ILogger<StorageServiceImpl> logger,
                              bool                        enableScoreRecalcWorker = true)
    {
        _rootDb = CreateClient(dbType, connectionString);
        _logger = logger;

        if (enableScoreRecalcWorker)
        {
            // The outbox worker owns a distinct SqlSugar scope. It must never share a request
            // transaction/connection with the foreground storage instance, and Shutdown waits for
            // its consumer before disposing this scope.
            _scoreRecalcWorkerStorage = new StorageServiceImpl(dbType, connectionString, logger,
                                                                enableScoreRecalcWorker: false);
            _scoreRecalcScheduler = new ScoreRecalcScheduler(
                token => _scoreRecalcWorkerStorage.RunOperationAsync(
                    _scoreRecalcWorkerStorage.ProcessScoreRecalcOutboxBatchAsync, token), logger);
        }
    }

    /// <summary>
    /// Initializes in-memory state and, when requested, applies this storage implementation's
    /// schema bootstrap/migrations. A horizontally scaled HTTP host should set
    /// <paramref name="initializeSchema"/> only on its designated migration instance;
    /// application replicas merely need the already-provisioned schema to serve reads.
    /// </summary>
    public void Init(bool initializeSchema = true, bool startScoreRecalcWorker = true)
    {
        _mapIdCache.Clear();
        _bestRunMapSeededCache.Clear();
        _bestRunSeededCache.Clear();

        if (!initializeSchema)
        {
            // Even replicas that skip CodeFirst must not interpret a legacy temporal Date as
            // the new BIGINT mapping or serve writes against an interrupted conversion.
            EnsureRunDateColumnReadyForRuntime();
            EnsurePointsColumnsReadyForRuntime();
            _logger.LogInformation("SQL schema initialization disabled; expecting an already migrated database.");
            if (startScoreRecalcWorker) StartScoreRecalcWorker();
            return;
        }

        RejectUnsupportedNonMasterRunSchema();
        EnsureRunDateColumnReadyForRuntime();
        EnsurePointsColumnsReadyForRuntime();
        MigrateReplaySteamIdColumn();

        try
        {
            _db.CodeFirst.InitTables(typeof(MapEntity),
                                     typeof(MapTrackEntity),
                                     typeof(PlayerEntity),
                                     typeof(PlayerMapStatsEntity),
                                     typeof(PlayerBestRunEntity),
                                     typeof(PlayerTrackScoreEntity),
                                     typeof(ScoreRecalcOutboxEntity),
                                     typeof(RunSubmissionEntity),
                                     typeof(RunEntity),
                                     typeof(RunSegmentEntity),
                                     typeof(ReplayEntity),
                                     typeof(ZoneEntity));
        }
        catch (Exception e)
        {
            // Propagate: a failed InitTables means the DB is unreachable/broken. Swallowing
            // it would report Init success, register this backend, and displace the working
            // Schema initialization must not report success after a failure.
            _logger.LogError(e, "Error when initializing tables");

            throw;
        }

        MigratePlayerJoinDates();
        RepairInvalidStoredPlayTimes();
        EnsureTrackScoreCoveringIndex();
        EnsureScoreRecalcOutboxIndexes();
        EnsureRunSubmissionInboxIndex();
        if (startScoreRecalcWorker) StartScoreRecalcWorker();
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
        if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
        if (_scoreRecalcScheduler is not null && Volatile.Read(ref _workerStopRequested) == 0)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { StopScoreRecalcWorkerAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Score worker shutdown exceeded 15 seconds; retaining its SQL scope until the active operation finishes.");
            }
        }
        _scoreRecalcScheduler?.Dispose();
        if (_scoreRecalcWorkerStorage is not null) _ = DisposeWorkerStorageAfterCompletionAsync();
        _rootDb.Dispose();
    }

    internal Task StopScoreRecalcWorkerAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _workerStopRequested, 1);
        return _scoreRecalcScheduler?.StopAsync(cancellationToken) ?? Task.CompletedTask;
    }

    internal void RequestScoreRecalcWorkerStop()
    {
        Interlocked.Exchange(ref _workerStopRequested, 1);
        _scoreRecalcScheduler?.RequestStop();
    }

    private async Task DisposeWorkerStorageAfterCompletionAsync()
    {
        try { await _scoreRecalcScheduler!.Completion; }
        catch (Exception exception) { _logger.LogError(exception, "Score worker stopped unexpectedly."); }
        finally { _scoreRecalcWorkerStorage!.Shutdown(); }
    }

    private void WakeScoreRecalcWorker()
        => _scoreRecalcScheduler?.Wake();

    internal void StartScoreRecalcWorker()
    {
        // Tests and hosts sometimes customize SqlSugar type mapping after constructing this
        // storage. The worker has an independent connection scope, so copy that configuration
        // before its first database operation rather than letting it diverge from the foreground.
        if (_scoreRecalcWorkerStorage is not null)
        {
            _scoreRecalcWorkerStorage.Db.CurrentConnectionConfig.ConfigureExternalServices =
                _db.CurrentConnectionConfig.ConfigureExternalServices;
        }

        if (_scoreRecalcScheduler is not null)
        {
            _workerStartedAtUtc = DateTime.UtcNow;
            _scoreRecalcScheduler.Start();
        }
    }

    public async Task<MapProfile> GetMapInfo(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await EnsureMapEntityByKeyAsync(mapKey, map);

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync(OperationCancellation);

        return ToMapProfile(mapInfo, trackTiers);
    }

    /// <summary>
    /// Reads an existing map profile without the legacy <see cref="GetMapInfo"/> side effect
    /// of creating a map row. HTTP GET endpoints must use this method so that they remain
    /// cacheable and cannot provision data merely by being probed.
    /// </summary>
    internal async Task<MapProfile?> GetExistingMapInfoAsync(string map)
    {
        var mapKey  = ToMapKey(map);
        var mapInfo = await FindMapByNameAsync(mapKey);

        if (mapInfo is null)
        {
            return null;
        }

        _mapIdCache[mapKey] = mapInfo.MapId;

        var trackTiers = await _db.Queryable<MapTrackEntity>()
                                  .Where(x => x.MapId == mapInfo.MapId)
                                  .ToListAsync(OperationCancellation);

        return ToMapProfile(mapInfo, trackTiers);
    }

    /// <summary>
    /// Executes strongly-typed database checks for readiness probes. It intentionally does not
    /// mutate maps, invoke lazy best-run seeding, or use raw ADO commands. A read-only backend
    /// only checks the map table; a write-serving backend additionally verifies the durable
    /// submission Inbox, score-recalculation Outbox, their correctness/performance indexes,
    /// and every table touched by the write transaction before it can accept a write.
    /// </summary>
    internal async Task CheckReadyAsync(bool requireWriteSchema = false)
    {
        const string mapTableName = "surf_maps";
        const string submissionTableName = "surf_run_submissions";
        const string outboxTableName = "surf_score_recalc_outbox";
        const string submissionIndexName = "idx_surf_run_submissions_submission_unique";
        const string outboxUniqueIndexName = "idx_score_recalc_outbox_unique";
        const string outboxPendingIndexName = "idx_score_recalc_outbox_pending";
        const string trackScoreCoveringIndexName = "idx_player_track_scores_map_style_track";

        if (!requireWriteSchema)
        {
            await _db.Queryable<MapEntity>()
                     .Select(x => x.MapId)
                     .Take(1)
                     .ToListAsync(OperationCancellation);
            return;
        }

        var missing = new List<string>();

        foreach (var tableName in new[]
                 {
                     mapTableName,
                     "surf_maps_tracks",
                     "surf_players",
                     "surf_player_best_runs",
                     "surf_player_track_scores",
                     "surf_runs",
                     "surf_runs_segments",
                     submissionTableName,
                     outboxTableName,
                 })
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                missing.Add($"table {tableName}");
            }
        }

        // The Inbox index is the correctness boundary for concurrent/retried submissions.
        // Check it only after confirming its table exists so a missing table produces the
        // more actionable table error rather than a provider-specific metadata failure.
        if (_db.DbMaintenance.IsAnyTable(submissionTableName, false)
            && !_db.DbMaintenance.IsAnyIndex(submissionIndexName))
        {
            missing.Add($"unique index {submissionIndexName} on {submissionTableName}");
        }

        if (_db.DbMaintenance.IsAnyTable(outboxTableName, false))
        {
            if (!_db.DbMaintenance.IsAnyIndex(outboxUniqueIndexName))
            {
                missing.Add($"unique index {outboxUniqueIndexName} on {outboxTableName}");
            }

            if (!_db.DbMaintenance.IsAnyIndex(outboxPendingIndexName))
            {
                missing.Add($"index {outboxPendingIndexName} on {outboxTableName}");
            }
        }

        if (_db.DbMaintenance.IsAnyTable("surf_player_track_scores", false)
            && !_db.DbMaintenance.IsAnyIndex(trackScoreCoveringIndexName))
        {
            missing.Add($"index {trackScoreCoveringIndexName} on surf_player_track_scores");
        }

        if (missing.Count != 0)
        {
            var role = requireWriteSchema ? "write" : "read-only";
            throw new InvalidOperationException(
                $"Timer backend {role} schema is not ready; missing {string.Join(", ", missing)}.");
        }

        await ValidateWriteIndexesAsync();

        // Metadata checks above give a stable operator-facing error for a missing table. These
        // typed probes also verify that the configured database principal can read every table
        // used by the selected role.
        // Select full typed rows even when the tables are empty. This makes a partially-created
        // table fail now instead of waiting for the first live submission to reference a missing
        // column. The result sets remain bounded to one row and are startup-only.
        await _db.Queryable<MapEntity>().Take(1).ToListAsync(OperationCancellation);
        // Every score recalculation joins map-track tiers, including main-track work.
        // Validate the worker's required table and columns before accepting submissions.
        await _db.Queryable<MapTrackEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<PlayerEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<PlayerBestRunEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<PlayerTrackScoreEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<RunEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<RunSegmentEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<RunSubmissionEntity>().Take(1).ToListAsync(OperationCancellation);
        await _db.Queryable<ScoreRecalcOutboxEntity>().Take(1).ToListAsync(OperationCancellation);
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
            // Map metadata only. PlayCount/TotalPlayTime are session counters and must
            // never be copied from an old per-server map-profile snapshot.
            // Do not copy a stale BasePot over an administrator's concurrent change.
            await _db.Updateable<MapEntity>()
                .SetColumns(x => x.File == mapKey)
                .SetColumns(x => x.Tier == tier)
                .SetColumns(x => x.Stages == stages)
                .SetColumns(x => x.Bonuses == info.Bonuses)
                .Where(x => x.MapId == mapId).ExecuteCommandAsync(OperationCancellation);
            await SyncMapTrackTiersAsync(mapId, info.Tier);
        });

        // Cache writes only after a successful commit, so a rollback can never leave
        // the caches holding state from a transaction that never happened.
        _mapIdCache[mapKey] = mapId;
    }

    public async Task IncrementMapStatsAsync(string mapName, float deltaSeconds)
    {
        ValidatePlayTimeDelta(deltaSeconds);
        var maximumCurrentTime = (double)MaximumStoredPlayTimeSeconds - deltaSeconds;
        var mapId = await EnsureMapIdByNameAsync(mapName);
        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            var updated = await _db.Updateable<MapEntity>()
                                   .SetColumns(x => x.PlayCount == x.PlayCount + 1)
                                   .SetColumns(x => x.TotalPlayTime == x.TotalPlayTime + deltaSeconds)
                                   .Where(x => x.MapId == mapId && x.PlayCount >= 0 && x.PlayCount < int.MaxValue
                                               && x.TotalPlayTime >= 0 && x.TotalPlayTime <= maximumCurrentTime)
                                   .ExecuteCommandAsync(OperationCancellation);
            if (updated != 1)
            {
                throw new InvalidOperationException("Map session counters could not be incremented.");
            }
        });
    }

}
