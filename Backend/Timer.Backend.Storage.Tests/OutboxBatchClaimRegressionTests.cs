using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class OutboxBatchClaimRegressionTests
{
    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlBlockedBatchHeadCannotDeadLetterUnstartedBoardsDuringShutdown()
        => BlockedBatchHeadCannotDeadLetterUnstartedBoardsDuringShutdown(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlBlockedBatchHeadCannotDeadLetterUnstartedBoardsDuringShutdown()
        => BlockedBatchHeadCannotDeadLetterUnstartedBoardsDuringShutdown(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [Fact]
    public async Task LaterBatchItemReceivesAFullLeaseAfterTheEarlierItemTakesLongerThanTheLease()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timer-batch-clock-{Guid.NewGuid():N}.db");
        StorageServiceImpl NewStore()
        {
            var store = new StorageServiceImpl(DbType.Sqlite, $"Data Source={path};Pooling=False",
                NullLogger<StorageServiceImpl>.Instance, false);
            store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
            {
                if (column.IsIdentity) column.DataType = "INTEGER";
            };
            return store;
        }

        var worker = NewStore();
        var observer = NewStore();
        using var firstRelease = new ManualResetEventSlim();
        using var secondRelease = new ManualResetEventSlim();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? operation = null;
        try
        {
            worker.Init(startScoreRecalcWorker: false);
            var first = await worker.GetMapInfo($"surf_clock_first_{Guid.NewGuid():N}");
            var second = await worker.GetMapInfo($"surf_clock_second_{Guid.NewGuid():N}");
            var now = new DateTime(2026, 9, 17, 1, 0, 0, DateTimeKind.Utc);
            await worker.EnqueueScoreRecalcAsync(first.MapId, 0, 0, 1, now);
            await worker.EnqueueScoreRecalcAsync(second.MapId, 0, 0, 1, now);
            var firstClaimAt = now.AddSeconds(5);
            var clockTicks = firstClaimAt.Ticks;
            var leasedReads = 0;
            worker.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                // The claim re-read of each item; the recalc's own lease check selects a constant.
                if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                    || sql.TrimStart().StartsWith("SELECT 1", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
                    || !sql.Contains("LeaseOwner", StringComparison.OrdinalIgnoreCase))
                    return;
                var position = Interlocked.Increment(ref leasedReads);
                var entered = position == 1 ? firstEntered : secondEntered;
                var release = position == 1 ? firstRelease : secondRelease;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Clock regression did not release the leased-row read.");
            };
            operation = Task.Run(() => worker.ProcessScoreRecalcOutboxBatchAsync(
                firstClaimAt, "clock-worker", () => new DateTime(Interlocked.Read(ref clockTicks), DateTimeKind.Utc)));
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var laterClaimAt = firstClaimAt.AddMinutes(3);
            Interlocked.Exchange(ref clockTicks, laterClaimAt.Ticks);
            firstRelease.Set();
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var tail = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                .Where(x => x.MapId == second.MapId).SingleAsync();
            Assert.Equal(laterClaimAt.AddMinutes(2), tail.LeaseUntilUtc);
            Assert.Equal(1, tail.AttemptCount);
            Assert.Equal(0, tail.ProcessedGeneration);
            secondRelease.Set();
            Assert.Equal(2, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            firstRelease.Set();
            secondRelease.Set();
            try
            {
                if (operation is not null) await operation.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                worker.Shutdown();
                observer.Shutdown();
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }

    private static async Task BlockedBatchHeadCannotDeadLetterUnstartedBoardsDuringShutdown(
        DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var setup = fixture.NewStore();
        var workers = Enumerable.Range(0, 8).Select(_ => fixture.NewStore()).ToArray();
        var observer = fixture.NewStore();
        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();

        var blocked = await setup.GetMapInfo($"surf_batch_head_{Guid.NewGuid():N}");
        var healthy = await setup.GetMapInfo($"surf_batch_tail_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000));
        await setup.GetPlayerProfile(player, "Batch claim regression");
        await setup.AddPlayerRecord(player, blocked.MapName, new RecordRequest { Time = 80 });
        await setup.AddPlayerRecord(player, healthy.MapName, new RecordRequest { Time = 90 });

        // Avoid seeding taking a map lock before the deliberately paused score transaction.
        foreach (var worker in workers)
        {
            await worker.RecalculateTrackScoresAsync(blocked.MapId, 0, 0, 1);
            await worker.RecalculateTrackScoresAsync(healthy.MapId, 0, 0, 1);
        }

        var now = DateTime.UtcNow;
        await setup.EnqueueScoreRecalcAsync(blocked.MapId, 0, 0, 2, now);
        await setup.EnqueueScoreRecalcAsync(healthy.MapId, 0, 0, 2, now);
        var head = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.MapId == blocked.MapId).SingleAsync();
        var tail = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.MapId == healthy.MapId).SingleAsync();
        Assert.True(head.Id < tail.Id);
        var firstClaimAt = head.AvailableAtUtc > tail.AvailableAtUtc ? head.AvailableAtUtc : tail.AvailableAtUtc;

        var cancellations = Enumerable.Range(0, workers.Length).Select(_ => new CancellationTokenSource()).ToArray();
        var pauses = workers.Select((worker, index) => new SqlPause(
            worker, index == 0 ? IsScoreRead : IsGenerationCheck)).ToArray();
        var operations = new List<Task<int>>();
        try
        {
            for (var index = 0; index < workers.Length; index++)
            {
                var worker = workers[index];
                var token = cancellations[index].Token;
                var owner = $"blocked-batch-{index}";
                var claimAt = firstClaimAt.AddMinutes(index * 2);
                operations.Add(Task.Run(() => worker.RunOperationAsync(
                    () => worker.ProcessScoreRecalcOutboxBatchAsync(claimAt, owner), token)));
                await pauses[index].Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(owner, (await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                    .Where(x => x.Id == head.Id).SingleAsync()).LeaseOwner);
            }

            // All eight deliveries are still working on the first board. None has reached the
            // second board, but preclaiming the entire batch has consumed its delivery budget too.
            await observer.ProcessScoreRecalcOutboxBatchAsync(
                firstClaimAt.AddMinutes(16), "expired-batch-scan");
        }
        finally
        {
            // A normal deployment/shutdown may cancel the remaining callbacks. Successful
            // completion of an old claimant must not be the only way to rescue untouched work.
            foreach (var cancellation in cancellations) cancellation.Cancel();
            foreach (var pause in pauses) pause.Release.Set();
            try
            {
                await Task.WhenAll(operations.Select(async operation =>
                {
                    try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (OperationCanceledException) { }
                }));
            }
            finally
            {
                foreach (var pause in pauses) pause.Dispose();
                foreach (var cancellation in cancellations) cancellation.Dispose();
            }
        }

        var remaining = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.Id == tail.Id).SingleAsync();
        Assert.Null(remaining.DeadLetteredAtUtc);
        Assert.Equal(0, remaining.AttemptCount);
        await observer.ProcessScoreRecalcOutboxBatchAsync(
            firstClaimAt.AddMinutes(18), "restarted-batch-worker");
        var recovered = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.Id == tail.Id).SingleAsync();
        Assert.Equal(recovered.RequestedGeneration, recovered.ProcessedGeneration);
    }

    private static bool IsGenerationCheck(string sql) => Regex.IsMatch(sql, @"^\s*SELECT\s+1\s+FROM", RegexOptions.IgnoreCase)
                                                        && sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase);

    private static bool IsScoreRead(string sql)
        => sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("surf_player_track_scores", StringComparison.OrdinalIgnoreCase);

    private static bool IsMapLock(string sql)
        => sql.Contains("surf_maps", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);

    private sealed class SqlPause : IDisposable
    {
        private readonly StorageServiceImpl _store;
        private int _used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();

        public SqlPause(StorageServiceImpl store, Func<string, bool> matches)
        {
            _store = store;
            store.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                if (!matches(sql) || Interlocked.Exchange(ref _used, 1) != 0) return;
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(45)))
                    throw new TimeoutException("Batch-claim regression did not release the SQL pause.");
            };
        }

        public void Dispose()
        {
            _store.Db.Aop.OnLogExecuting = null;
            Release.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly DbType _type;
        private readonly string _connection;
        private readonly List<StorageServiceImpl> _stores = [];

        public Fixture(DbType type, string variable)
        {
            _type = type;
            _connection = Environment.GetEnvironmentVariable(variable)!;
            var parsed = new DbConnectionStringBuilder { ConnectionString = _connection };
            var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
            Assert.True(host is "127.0.0.1" or "localhost" or "::1"
                && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase),
                "Batch-claim acceptance requires a disposable loopback database with 'test' in its name.");
            var setup = NewStore();
            setup.Db.DbMaintenance.CreateDatabase();
            setup.Init(startScoreRecalcWorker: false);
        }

        public StorageServiceImpl NewStore()
        {
            var store = new StorageServiceImpl(_type, _connection, NullLogger<StorageServiceImpl>.Instance, false);
            _stores.Add(store);
            return store;
        }

        public void Dispose()
        {
            foreach (var store in _stores) store.Shutdown();
        }
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
