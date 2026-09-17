using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;

namespace Timer.RequestManager.Backend;

/// <summary>
/// Process-lifetime host for the existing Timer SQL storage implementation.
///
/// This deliberately exposes only the backend's bounded read operations, rather than the
/// SqlSugar context or IRequestManager's mutation surface. The web host can reuse the
/// established query semantics without letting transport code issue ad-hoc database work.
/// </summary>
public sealed partial class TimerBackendStorage : IDisposable
{
    private readonly StorageServiceImpl _storage;
    private readonly object _lifecycleLock = new ();
    private bool _allowReadRepair;
    private bool _requireWriteSchema;
    private int _startAttempted;
    private int _started;
    private int _disposed;

    internal TimerBackendStorage(StorageServiceImpl storage)
        => _storage = storage ?? throw new ArgumentNullException(nameof(storage));

    internal TimerBackendStorage(DbType dbType,
                                 string connectionString,
                                 ILoggerFactory loggerFactory,
                                 bool enableOutboxWorker)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _storage = new StorageServiceImpl(dbType,
                                          connectionString,
                                          loggerFactory.CreateLogger<StorageServiceImpl>(),
                                          enableScoreRecalcWorker: enableOutboxWorker);
    }

    /// <summary>
    /// Starts storage. Schema initialization is deliberately explicit so only a designated
    /// deployment/migration instance performs DDL in a scaled backend deployment. Read repair
    /// is also explicit because it writes the historical best-run projection. A write-serving
    /// instance performs a schema preflight before it is marked started; a missing Inbox,
    /// Outbox, or Inbox idempotency index therefore fails startup instead of exposing a write
    /// endpoint that cannot provide its transaction guarantees.
    /// </summary>
    public void Start(bool initializeSchema, bool allowReadRepair, bool requireWriteSchema = false)
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();

            if (Interlocked.CompareExchange(ref _startAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException("Timer backend storage has already started.");
            }

            try
            {
                _requireWriteSchema = requireWriteSchema || _storage.HasScoreRecalcWorker;
                _storage.Init(initializeSchema, startScoreRecalcWorker: false);

                if (_requireWriteSchema)
                {
                    _storage.CheckReadyAsync(requireWriteSchema: true).GetAwaiter().GetResult();
                }

                _allowReadRepair = allowReadRepair;
                _storage.StartScoreRecalcWorker();
                Volatile.Write(ref _started, 1);
            }
            catch
            {
                // Storage owns its SqlSugar scope from construction onward. Dispose it even when
                // schema initialization fails, but do not report the host as started.
                try
                {
                    _storage.Shutdown();
                }
                finally
                {
                    Volatile.Write(ref _disposed, 1);
                }

                throw;
            }
        }
    }

    /// <summary>
    /// Applies the explicit master-to-backend SQL migration without starting an API
    /// host or a score worker. The process should exit after this call succeeds.
    /// </summary>
    public void MigrateMasterDatabase()
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _startAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException("Timer backend storage has already started or migrated.");
            }

            // Migration runs only in the one-shot CLI. Keep lifecycle disposal
            // serialized with the whole operation, including projection seeding.
            _storage.MigrateMasterDatabaseAsync().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Converts the published master <c>surf_runs.Date</c> column before the
    /// additive backend migration.  It is intentionally a separate one-shot
    /// lifecycle operation so schema bootstrap cannot silently reinterpret an
    /// existing SQL date-time column.
    /// </summary>
    public void ConvertMasterRunDates(bool backupConfirmed)
    {
        lock (_lifecycleLock)
        {
            ThrowIfDisposed();
            if (Interlocked.CompareExchange(ref _startAttempted, 1, 0) != 0)
            {
                throw new InvalidOperationException("Timer backend storage has already started, migrated, or converted run dates.");
            }

            _storage.ConvertMasterRunDateColumnAsync(backupConfirmed).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Gets a map only when it already exists. Unlike IRequestManager.GetMapInfo, this has no
    /// map-provisioning side effect and is therefore safe for an HTTP GET endpoint.
    /// </summary>
    public Task<MapProfile?> GetExistingMapInfoAsync(string mapName, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetExistingMapInfoAsync(mapName), cancellationToken);
    }

    /// <summary>
    /// Checks map existence by its cached/strongly-typed map identity only. This deliberately
    /// avoids loading map-track tiers when a route only needs to distinguish 404 from an empty
    /// leaderboard or player result.
    /// </summary>
    public async Task<bool> MapExistsAsync(string mapName, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return await ExecuteAsync(() => _storage.ResolveMapIdByNameAsync(mapName), cancellationToken) is not null;
    }

    /// <summary>
    /// Performs a role-aware strongly typed database query for readiness probes. Read-only
    /// instances check only the map table; write instances also check the Inbox/Outbox schema
    /// and idempotency index.
    /// </summary>
    public Task CheckReadyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(async () => { await _storage.CheckReadyAsync(_requireWriteSchema); return true; }, cancellationToken);
    }

    public Task<IReadOnlyList<string>> GetAllMapNamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(_storage.GetAllMapNamesAsync, cancellationToken);
    }

    /// <summary>
    /// Backend-local administrative operation that changes one map's main-track tier and queues
    /// its affected configured main-track boards atomically. It is intentionally not part of the
    /// HTTP or gRPC transport surface.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> SetMapTierAndRequeueScoresAsync(
        string mapName,
        byte tier,
        IReadOnlyDictionary<int, double> styleFactors,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.SetMapTierAndRequeueScoresAsync(mapName, tier, styleFactors), cancellationToken);
    }

    /// <summary>
    /// Backend-local policy refresh for one map. Compatible dead-lettered score jobs are made
    /// pending again without waiting for another personal best or server record.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> RequeueMapScorePolicyAsync(
        string mapName,
        IReadOnlyDictionary<int, double> styleFactors,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.RequeueMapScorePolicyAsync(mapName, styleFactors), cancellationToken);
    }

    /// <summary>
    /// Backend-local policy refresh for every current map. Each map is committed independently
    /// under its own map lock, instead of taking a global administrative lock.
    /// </summary>
    public Task<TimerBackendScoreAdministrationResult> RequeueAllScorePoliciesAsync(
        IReadOnlyDictionary<int, double> styleFactors,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.RequeueAllScorePoliciesAsync(styleFactors), cancellationToken);
    }

    /// <summary>
    /// Retrieves a bounded leaderboard with independently optional filters. This is exposed
    /// for the HTTP read API because the historical IRequestManager overloads require style,
    /// track and stage together.
    /// </summary>
    public Task<IReadOnlyList<RunRecord>> GetMapRecordsForReadApiAsync(string mapName,
                                                                      bool   stageRecords,
                                                                      int?   style,
                                                                      int?   track,
                                                                      int?   stage,
                                                                      int    limit,
                                                                      CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetMapRecordsForReadApiAsync(mapName,
                                                     stageRecords,
                                                     style,
                                                     track,
                                                     stage,
                                                     limit,
                                                     _allowReadRepair), cancellationToken);
    }

    public Task<IReadOnlyList<RunRecord>> GetPlayerRecordsForReadApiAsync(ulong  steamId,
                                                                          string mapName,
                                                                          bool   stageRecords,
                                                                          int    limit,
                                                                          CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetPlayerRecordsForReadApiAsync(steamId,
                                                        mapName,
                                                        stageRecords,
                                                        limit,
                                                        _allowReadRepair), cancellationToken);
    }

    public Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpointsAsync(long runId, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetRecordCheckpoints(runId), cancellationToken);
    }

    public Task<(int rank, int total)> GetPlayerPointsRankAsync(ulong steamId, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetPlayerPointsRankForReadApiAsync(steamId), cancellationToken);
    }

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(ulong steamId, string mapName, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetPlayerMapStatsForReadApiAsync(steamId, mapName), cancellationToken);
    }

    internal Task<TimerBackendRunSubmissionResult> SubmitRunAsync(
        TimerBackendRunSubmissionCommand command, bool acceptNewWrites = true, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.SubmitBackendRunAsync(command, acceptNewWrites), cancellationToken);
    }

    internal Task<TimerBackendRunSubmissionResult?> GetSubmissionStatusAsync(Guid submissionId, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.GetBackendSubmissionStatusAsync(submissionId), cancellationToken);
    }

    internal Task<TimerBackendPlayerProfileResult> EnsurePlayerProfileAsync(TimerBackendPlayerProfileCommand command, CancellationToken cancellationToken = default)
    {
        ThrowIfNotStarted();
        return ExecuteAsync(() => _storage.EnsureBackendPlayerProfileAsync(command), cancellationToken);
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            // StorageServiceImpl is safe to stop after construction, which covers DI disposal if
            // host startup is cancelled before StartAsync.
            _storage.RequestScoreRecalcWorkerStop();
            if (_activeOperations == 0) _storage.Shutdown();
        }
    }

    private void ThrowIfNotStarted()
    {
        ThrowIfDisposed();

        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("Timer backend storage has not started.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(TimerBackendStorage));
        }
    }
}

/// <summary>
/// Creates a standalone storage host from a transport-neutral database configuration.
/// Kept here so the API host does not need to reference SqlSugar implementation types.
/// </summary>
public static class TimerBackendStorageFactory
{
    public static TimerBackendStorage Create(string databaseType,
                                             string connectionString,
                                             ILoggerFactory loggerFactory,
                                             bool enableOutboxWorker = false)
    {
        if (string.IsNullOrWhiteSpace(databaseType))
        {
            throw new ArgumentException("A database type is required.", nameof(databaseType));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A database connection string is required.", nameof(connectionString));
        }

        var dbType = databaseType.Trim().ToLowerInvariant() switch
        {
            "mysql" or "mariadb" => DbType.MySql,
            "pgsql" or "postgres" or "postgresql" => DbType.PostgreSQL,
            _ => throw new NotSupportedException($"Unsupported database type '{databaseType}'. Use mysql or postgresql."),
        };

        return new TimerBackendStorage(dbType, connectionString, loggerFactory, enableOutboxWorker);
    }
}
