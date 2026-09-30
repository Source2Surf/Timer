using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.RequestManager.Backend;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseCancellationTests
{
    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlLockWaitCanBeCancelledAndRetried()
        => LockWaitCanBeCancelledAndRetried(SqlSugar.DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlLockWaitCanBeCancelledAndRetried()
        => LockWaitCanBeCancelledAndRetried(SqlSugar.DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task LockWaitCanBeCancelledAndRetried(SqlSugar.DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        EnsureDisposableDatabase(type, connection);

        var holder = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        var writer = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        using var owner = new TimerBackendStorage(writer);
        try
        {
            holder.Init();
            owner.Start(false, false, requireWriteSchema: true);
            var map = await holder.GetMapInfo($"surf_cancel_{Guid.NewGuid():N}");
            var steamId = 76561198000000000L + Random.Shared.NextInt64(1, 1_000_000_000);
            await holder.GetPlayerProfile(new SteamID(checked((ulong)steamId)), "Cancellation acceptance");
            var command = new TimerBackendRunSubmissionCommand
            {
                SubmissionId = Guid.NewGuid(), SteamId = steamId, MapName = map.MapName,
                Kind = TimerBackendRunKind.Main, TimeMicros = 80_000_000,
                FinishedAtUtc = DateTime.UtcNow, RulesetVersion = 1, StyleFactor = 1,
            };
            var writes = new TimerBackendWriteStorage(owner);
            using var cancellation = new CancellationTokenSource();
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<TimerBackendRunSubmissionResult>? request = null;
            await holder.Db.Ado.BeginTranAsync(IsolationLevel.ReadCommitted);
            try
            {
                await holder.Db.Queryable<MapEntity>().Where(x => x.MapId == map.MapId)
                    .TranLock(DbLockType.Wait).FirstAsync();
                request = Task.Run(async () =>
                {
                    writer.Db.Aop.OnLogExecuting = (sql, _) =>
                    {
                        if (sql.Contains("surf_maps", StringComparison.OrdinalIgnoreCase)
                            && sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                            attempted.TrySetResult();
                    };
                    return await writes.SubmitRunAsync(command, cancellation.Token);
                });
                await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(50);
                Assert.False(request.IsCompleted);

                // The same singleton's independent read must not inherit the writer's
                // token or transaction while a different connection holds the map lock.
                var names = await owner.GetAllMapNamesAsync().WaitAsync(TimeSpan.FromSeconds(3));
                Assert.Contains(map.MapName, names);
                var watch = Stopwatch.StartNew();
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => request.WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3));
            }
            finally
            {
                await holder.Db.Ado.RollbackTranAsync();
                cancellation.Cancel();
                if (request is not null)
                {
                    try { await request.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (OperationCanceledException) { }
                }
            }

            Assert.Null(await writes.GetSubmissionStatusAsync(command.SubmissionId));
            Assert.Equal(0, await holder.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
            var accepted = await writes.SubmitRunAsync(command).WaitAsync(TimeSpan.FromSeconds(5));
            var retry = await writes.SubmitRunAsync(command).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(TimerBackendSubmissionDisposition.Accepted, accepted.Disposition);
            Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, retry.Disposition);
            Assert.Equal(accepted.RunId, retry.RunId);
            Assert.Equal(1, await holder.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
        }
        finally { holder.Shutdown(); }
    }

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlWorkerShutdownReleasesLeaseAndRestartProcessesPendingWork()
        => WorkerShutdownReleasesLeaseAndRestartProcessesPendingWork(SqlSugar.DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlWorkerShutdownReleasesLeaseAndRestartProcessesPendingWork()
        => WorkerShutdownReleasesLeaseAndRestartProcessesPendingWork(SqlSugar.DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task WorkerShutdownReleasesLeaseAndRestartProcessesPendingWork(SqlSugar.DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        EnsureDisposableDatabase(type, connection);
        var holder = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        var observer = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        TimerBackendStorage NewWorker()
            => new(new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, true));
        try
        {
            holder.Init();
            await holder.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
            var map = await holder.GetMapInfo($"surf_worker_restart_{Guid.NewGuid():N}");
            var steamId = 76561198000000000L + Random.Shared.NextInt64(1, 1_000_000_000);
            await holder.GetPlayerProfile(new SteamID(checked((ulong)steamId)), "Worker restart");
            var result = await holder.SubmitBackendRunAsync(new TimerBackendRunSubmissionCommand
            {
                SubmissionId = Guid.NewGuid(), SteamId = steamId, MapName = map.MapName,
                Kind = TimerBackendRunKind.Main, TimeMicros = 80_000_000,
                FinishedAtUtc = DateTime.UtcNow, RulesetVersion = 1, StyleFactor = 1,
            });
            var available = DateTime.UtcNow.AddSeconds(-1);
            await holder.Db.Updateable<ScoreRecalcOutboxEntity>()
                .SetColumns(x => x.AvailableAtUtc == available).Where(x => x.MapId == map.MapId)
                .ExecuteCommandAsync();

            using (var stopping = NewWorker())
            {
                await holder.Db.Ado.BeginTranAsync(IsolationLevel.ReadCommitted);
                try
                {
                    await holder.Db.Queryable<MapEntity>().Where(x => x.MapId == map.MapId)
                        .TranLock(DbLockType.Wait).FirstAsync();
                    stopping.Start(false, false);
                    await WaitForOutboxAsync(observer, map.MapId, row => row.LeaseOwner is not null);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await stopping.StopWorkerAsync(deadline.Token);
                    var pending = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                        .Where(x => x.MapId == map.MapId).FirstAsync();
                    Assert.Null(pending.LeaseOwner);
                    Assert.Null(pending.DeadLetteredAtUtc);
                    Assert.Equal(0, pending.AttemptCount);
                    Assert.Equal(0, pending.ProcessedGeneration);
                }
                finally { await holder.Db.Ado.RollbackTranAsync(); }
            }

            using (var restarted = NewWorker())
            {
                restarted.Start(false, false);
                await WaitForOutboxAsync(observer, map.MapId,
                    row => row.ProcessedGeneration == row.RequestedGeneration);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await restarted.StopWorkerAsync(deadline.Token);
            }
            Assert.Equal(1, await observer.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).CountAsync());
            Assert.Equal(result.RunId, (await observer.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).FirstAsync()).Id);
            var points = await observer.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamId)
                .Select(x => x.Points).FirstAsync();
            Assert.True(points > 0);
        }
        finally
        {
            holder.Shutdown();
            observer.Shutdown();
        }
    }

    private static async Task WaitForOutboxAsync(StorageServiceImpl store, ulong mapId, Func<ScoreRecalcOutboxEntity, bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var row = await store.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.MapId == mapId)
                .FirstAsync(deadline.Token);
            if (condition(row)) return;
            await Task.Delay(25, deadline.Token);
        }
    }

    private static void EnsureDisposableDatabase(SqlSugar.DbType type, string connection)
    {
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
        var host = Convert.ToString(parsed[type == SqlSugar.DbType.MySql ? "Server" : "Host"]);
        var database = Convert.ToString(parsed["Database"]);
        Assert.True(host is "127.0.0.1" or "localhost" or "::1"
            && database!.Contains("test", StringComparison.OrdinalIgnoreCase),
            "Cancellation acceptance requires a disposable loopback database with 'test' in its name.");
    }

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}
