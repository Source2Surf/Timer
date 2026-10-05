using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseReviewRegressionTests
{
    [DatabaseTheory("TIMER_TEST_MYSQL")]
    [InlineData(false)]
    [InlineData(true)]
    public Task MySqlLongRecalculationCommitsItsCompletionAfterLeaseTakeover(bool exhaustDeliveries)
        => LongRecalculationCommitsItsCompletion(DbType.MySql, "TIMER_TEST_MYSQL", exhaustDeliveries);

    [DatabaseTheory("TIMER_TEST_POSTGRES")]
    [InlineData(false)]
    [InlineData(true)]
    public Task PostgreSqlLongRecalculationCommitsItsCompletionAfterLeaseTakeover(bool exhaustDeliveries)
        => LongRecalculationCommitsItsCompletion(DbType.PostgreSQL, "TIMER_TEST_POSTGRES", exhaustDeliveries);

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlWipeDuringRecalculationLeavesNoScores()
        => WipeDuringRecalculationLeavesNoScores(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlWipeDuringRecalculationLeavesNoScores()
        => WipeDuringRecalculationLeavesNoScores(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlOvertakenRecalculationCannotCommitOverANewerOne()
        => OvertakenRecalculationCannotCommitOverANewerOne(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlOvertakenRecalculationCannotCommitOverANewerOne()
        => OvertakenRecalculationCannotCommitOverANewerOne(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlConcurrentZoneSavesReplaceWholeSnapshots()
        => ConcurrentZoneSavesReplaceWholeSnapshots(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlConcurrentZoneSavesReplaceWholeSnapshots()
        => ConcurrentZoneSavesReplaceWholeSnapshots(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlOutboxAgeMigrationPreservesExistingPendingWork()
        => OutboxAgeMigrationPreservesExistingPendingWork(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlOutboxAgeMigrationPreservesExistingPendingWork()
        => OutboxAgeMigrationPreservesExistingPendingWork(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlWorkerHealthAggregationMatchesOutboxState()
        => WorkerHealthAggregationMatchesOutboxState(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlWorkerHealthAggregationMatchesOutboxState()
        => WorkerHealthAggregationMatchesOutboxState(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task WorkerHealthAggregationMatchesOutboxState(DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var setup = fixture.NewStore();
        var healthReader = fixture.NewStore(worker: true);
        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();

        var now = DateTime.UtcNow;
        var oldest = now.AddMinutes(-2);
        var newer = now.AddMinutes(-1);
        await setup.Db.Insertable(new[]
        {
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 0, RequestedGeneration = 1, ProcessedGeneration = 0,
                StyleFactor = 1, PendingSinceUtc = null, AvailableAtUtc = now,
                CreatedAtUtc = oldest, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 1, RequestedGeneration = 2, ProcessedGeneration = 1,
                StyleFactor = 1, PendingSinceUtc = newer, AvailableAtUtc = now.AddMinutes(5),
                LeaseOwner = "future-worker", LeaseUntilUtc = now.AddMinutes(5),
                CreatedAtUtc = now, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 2, RequestedGeneration = 3, ProcessedGeneration = 3,
                StyleFactor = 1, AvailableAtUtc = now, DeadLetteredAtUtc = now,
                CreatedAtUtc = now, UpdatedAtUtc = now,
            },
            new ScoreRecalcOutboxEntity
            {
                MapId = 999, Style = 3, RequestedGeneration = 4, ProcessedGeneration = 4,
                StyleFactor = 1, AvailableAtUtc = now, CreatedAtUtc = now, UpdatedAtUtc = now,
            },
        }).ExecuteCommandAsync();

        var health = await healthReader.GetWorkerHealthAsync();
        Assert.True(health.Enabled);
        Assert.Equal(2, health.PendingCount);
        Assert.Equal(1, health.DeadLetterCount);
        Assert.Equal(DateTimeKind.Utc, health.OldestPendingSinceUtc!.Value.Kind);
        Assert.InRange(Math.Abs((health.OldestPendingSinceUtc.Value - oldest).TotalSeconds), 0, 1);

        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
        health = await healthReader.GetWorkerHealthAsync();
        Assert.Equal(0, health.PendingCount);
        Assert.Equal(0, health.DeadLetterCount);
        Assert.Null(health.OldestPendingSinceUtc);

        var disabled = fixture.NewStore();
        var disabledSql = 0;
        disabled.Db.Aop.OnLogExecuting = (_, _) => Interlocked.Increment(ref disabledSql);
        try
        {
            var disabledHealth = await disabled.GetWorkerHealthAsync();
            Assert.False(disabledHealth.Enabled);
            Assert.Equal(0, Volatile.Read(ref disabledSql));
        }
        finally
        {
            disabled.Db.Aop.OnLogExecuting = null;
        }
    }

    private static async Task OutboxAgeMigrationPreservesExistingPendingWork(DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var store = fixture.NewStore();
        var healthReader = fixture.NewStore(worker: true);
        await store.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
        var map = await store.GetMapInfo($"surf_age_migration_{Guid.NewGuid():N}");
        var now = DateTime.UtcNow;
        var original = now.AddHours(-1);
        await store.Db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = map.MapId, RequestedGeneration = 1, ProcessedGeneration = 0,
            StyleFactor = 1, AvailableAtUtc = now.AddMinutes(5), CreatedAtUtc = original, UpdatedAtUtc = now,
        }).ExecuteCommandAsync();
        // Recreate the previous source bundle's populated Outbox shape.
        Assert.True(store.Db.DbMaintenance.DropColumn("surf_score_recalc_outbox", "PendingSinceUtc"));
        await Assert.ThrowsAnyAsync<Exception>(() => store.CheckReadyAsync(requireWriteSchema: true));
        await store.MigrateMasterDatabaseAsync();
        await store.CheckReadyAsync(requireWriteSchema: true);
        var health = await healthReader.GetWorkerHealthAsync();
        Assert.Equal(1, health.PendingCount);
        Assert.InRange(Math.Abs((health.OldestPendingSinceUtc!.Value - original).TotalSeconds), 0, 1);

        await store.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 2, now);
        Assert.InRange(Math.Abs(((await healthReader.GetWorkerHealthAsync()).OldestPendingSinceUtc!.Value - original).TotalSeconds), 0, 1);
        var pending = await store.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.MapId == map.MapId).SingleAsync();
        Assert.Equal(1, await store.ProcessScoreRecalcOutboxBatchAsync(pending.AvailableAtUtc, "migrated-age-worker"));
        Assert.Null((await healthReader.GetWorkerHealthAsync()).OldestPendingSinceUtc);

        var next = now.AddMinutes(1);
        await store.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, next);
        Assert.InRange(Math.Abs(((await healthReader.GetWorkerHealthAsync()).OldestPendingSinceUtc!.Value - next).TotalSeconds), 0, 1);
    }

    private static async Task LongRecalculationCommitsItsCompletion(DbType type, string variable, bool exhaustDeliveries)
    {
        using var fixture = new Fixture(type, variable);
        var first = fixture.NewStore();
        var followers = Enumerable.Range(0, exhaustDeliveries ? 7 : 1).Select(_ => fixture.NewStore()).ToArray();
        var observer = fixture.NewStore();
        await first.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
        var map = await first.GetMapInfo($"surf_handoff_{Guid.NewGuid():N}");
        var player = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000));
        await first.GetPlayerProfile(player, "Lease handoff");
        await first.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 80 });
        // Warm independent seed gates before observing the score transaction.
        await first.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        foreach (var follower in followers) await follower.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var now = DateTime.UtcNow;
        await first.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 2, now);
        var pending = await observer.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.MapId == map.MapId).SingleAsync();
        var followerScoreReads = 0;
        using var firstPause = new SqlPause(first, IsScoreRead);
        var followerPauses = followers.Select(follower => new SqlPause(follower, sql =>
        {
            if (IsScoreRead(sql)) Interlocked.Increment(ref followerScoreReads);
            return IsGenerationCheck(sql);
        })).ToArray();
        var firstWork = Task.Run(() => first.ProcessScoreRecalcOutboxBatchAsync(pending.AvailableAtUtc, "long-owner"));
        var followerWork = new List<Task<int>>();
        try
        {
            await firstPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var index = 0; index < followers.Length; index++)
            {
                var worker = followers[index];
                var owner = $"replacement-{index}";
                var claimAt = pending.AvailableAtUtc.AddMinutes((index + 1) * 2);
                followerWork.Add(Task.Run(() => worker.ProcessScoreRecalcOutboxBatchAsync(claimAt, owner)));
                await followerPauses[index].Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(owner, (await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                    .Where(x => x.Id == pending.Id).SingleAsync()).LeaseOwner);
            }
            if (exhaustDeliveries)
            {
                // Eight real claims expired while the first score transaction was still active.
                Assert.Equal(1, await observer.ProcessScoreRecalcOutboxBatchAsync(
                    pending.AvailableAtUtc.AddMinutes(16), "expiry-scan"));
                Assert.NotNull((await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                    .Where(x => x.Id == pending.Id).SingleAsync()).DeadLetteredAtUtc);
            }
            firstPause.Release.Set();
            await firstWork.WaitAsync(TimeSpan.FromSeconds(10));
            var completed = await observer.Db.Queryable<ScoreRecalcOutboxEntity>()
                .Where(x => x.Id == pending.Id).SingleAsync();
            Assert.Equal(completed.RequestedGeneration, completed.ProcessedGeneration);
            Assert.Null(completed.LeaseOwner);
            Assert.Null(completed.DeadLetteredAtUtc);
            Assert.Equal(0, completed.AttemptCount);
        }
        finally
        {
            firstPause.Release.Set();
            foreach (var pause in followerPauses) pause.Release.Set();
            try { await Task.WhenAll(followerWork.Append(firstWork)).WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { foreach (var pause in followerPauses) pause.Dispose(); }
        }
        Assert.Equal(0, followerScoreReads); // Waiters see completed generation and never recalculate it.
    }

    // Recalculations take no map lock. A wipe that lands while one has uncommitted scores must
    // still end with no scores on the map and every total equal to the remaining scores.
    private static async Task WipeDuringRecalculationLeavesNoScores(DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var setup = fixture.NewStore();
        var worker = fixture.NewStore();
        var wiper = fixture.NewStore();
        var observer = fixture.NewStore();
        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();

        var wiped = await setup.GetMapInfo($"surf_wiped_{Guid.NewGuid():N}");
        var kept = await setup.GetMapInfo($"surf_kept_{Guid.NewGuid():N}");
        var p = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000));
        var q = new SteamID(p.AsPrimitive() + 1);
        await setup.GetPlayerProfile(p, "Wiped and kept");
        await setup.GetPlayerProfile(q, "Wiped only");
        await setup.AddPlayerRecord(p, wiped.MapName, new RecordRequest { Time = 80 });
        await setup.AddPlayerRecord(q, wiped.MapName, new RecordRequest { Time = 90 });
        await setup.AddPlayerRecord(p, kept.MapName, new RecordRequest { Time = 70 });
        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();
        await setup.RecalculateTrackScoresAsync(kept.MapId, 0, 0, 1);

        var now = DateTime.UtcNow;
        await worker.EnqueueScoreRecalcAsync(wiped.MapId, 0, 0, 1, now);
        var queued = await observer.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.MapId == wiped.MapId).SingleAsync();

        Task? wipe = null;
        using (var pause = new SqlPause(worker, IsOutboxCompletion))
        {
            var recalc = Task.Run(() => worker.ProcessScoreRecalcOutboxBatchAsync(queued.AvailableAtUtc, "in-flight"));
            try
            {
                await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                wipe = Task.Run(() => wiper.RemoveMapRecords(wiped.MapName));
                await Task.Delay(500);
            }
            finally
            {
                pause.Release.Set();
                await Task.WhenAll(recalc, wipe ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        while (await worker.ProcessScoreRecalcOutboxBatchAsync(now.AddMinutes(5), $"drain-{Guid.NewGuid():N}") > 0)
        {
        }

        Assert.Equal(0, await observer.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.MapId == wiped.MapId).CountAsync());
        foreach (var player in new[] { p, q })
        {
            var id = unchecked((long)player.AsPrimitive());
            var scores = await observer.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.SteamId == id).ToListAsync();
            var total = (await observer.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == id).SingleAsync()).Points;
            Assert.Equal(scores.Sum(x => (long)x.Points), (long)total);
        }

        Assert.True((await observer.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == unchecked((long)p.AsPrimitive())).SingleAsync()).Points > 0);
    }

    // A recalc outlives its lease while a PB queues a newer generation; a second worker claims that
    // generation and reads scores before the first commits. Only one of them may commit.
    private static async Task OvertakenRecalculationCannotCommitOverANewerOne(DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var setup = fixture.NewStore();
        var slow = fixture.NewStore();
        var takeover = fixture.NewStore();
        var observer = fixture.NewStore();
        await setup.Db.Deleteable<ScoreRecalcOutboxEntity>().ExecuteCommandAsync();

        var map = await setup.GetMapInfo($"surf_overtaken_{Guid.NewGuid():N}");
        var first = 76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000);
        var (p, q, r) = (new SteamID(first), new SteamID(first + 1), new SteamID(first + 2));
        foreach (var (player, time) in new[] { (p, 50f), (q, 60f), (r, 70f) })
        {
            await setup.GetPlayerProfile(player, "Overtaken");
            await setup.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = time });
        }

        while (await setup.ProcessScoreRecalcOutboxBatchAsync(DateTime.UtcNow.AddMinutes(10), $"seed-{Guid.NewGuid():N}") > 0)
        {
        }

        await setup.AddPlayerRecord(q, map.MapName, new RecordRequest { Time = 40 });
        var queued = await observer.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.MapId == map.MapId).SingleAsync();
        using (var slowPause = new SqlPause(slow, IsOutboxCompletion))
        using (var takeoverPause = new SqlPause(takeover, IsOutboxCompletion))
        {
            var slowRun = Task.Run(() => slow.ProcessScoreRecalcOutboxBatchAsync(queued.AvailableAtUtc, "slow-owner"));
            var takeoverRun = Task.CompletedTask;
            try
            {
                await slowPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await setup.AddPlayerRecord(p, map.MapName, new RecordRequest { Time = 30 });
                takeoverRun = Task.Run(() => takeover.ProcessScoreRecalcOutboxBatchAsync(queued.AvailableAtUtc.AddMinutes(3), "takeover-owner"));
                await takeoverPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                slowPause.Release.Set();
                await slowRun.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                slowPause.Release.Set();
                takeoverPause.Release.Set();
                await Task.WhenAll(slowRun, takeoverRun).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        async Task<uint> Score(SteamID player)
        {
            var id = unchecked((long)player.AsPrimitive());
            return (await observer.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.MapId == map.MapId && x.SteamId == id).SingleAsync()).Points;
        }

        // The board is P 30, Q 40, R 70.
        Assert.True(await Score(p) > await Score(q));
        Assert.True(await Score(q) > await Score(r));
        var done = await observer.Db.Queryable<ScoreRecalcOutboxEntity>().Where(x => x.Id == queued.Id).SingleAsync();
        Assert.Equal(done.RequestedGeneration, done.ProcessedGeneration);
        foreach (var player in new[] { p, q, r })
        {
            var id = unchecked((long)player.AsPrimitive());
            var total = (await observer.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == id).SingleAsync()).Points;
            Assert.Equal(await Score(player), total);
        }
    }

    private static bool IsOutboxCompletion(string sql)
        => sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)
           && sql.Contains("ProcessedGeneration", StringComparison.OrdinalIgnoreCase)
           && sql.IndexOf("ProcessedGeneration", StringComparison.OrdinalIgnoreCase) < sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);

    private static async Task ConcurrentZoneSavesReplaceWholeSnapshots(DbType type, string variable)
    {
        using var fixture = new Fixture(type, variable);
        var first = fixture.NewStore();
        var second = fixture.NewStore();
        var map = await first.GetMapInfo($"surf_zones_{Guid.NewGuid():N}");
        using var firstPause = new SqlPause(first, sql => IsCommand(sql, "INSERT", "surf_zones"));
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (IsMapLock(sql) || IsCommand(sql, "DELETE", "surf_zones")) secondEntered.TrySetResult();
        };
        var firstSave = Task.Run(() => first.SaveZonesAsync(map.MapName,
            [new ZoneData { Sequence = 1, Config = "first snapshot" }]));
        Task? secondSave = null;
        try
        {
            await firstPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondSave = Task.Run(() => second.SaveZonesAsync(map.MapName,
                [new ZoneData { Sequence = 2, Config = "second snapshot" }]));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
        }
        finally
        {
            firstPause.Release.Set();
            await Task.WhenAll(firstSave, secondSave ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var final = Assert.Single(await first.GetZonesAsync(map.MapName));
        Assert.Equal("second snapshot", final.Config);
    }

    private static bool IsCommand(string sql, string command, string table)
        => sql.TrimStart().StartsWith(command, StringComparison.OrdinalIgnoreCase)
            && sql.Contains(table, StringComparison.OrdinalIgnoreCase);
    private static bool IsMapLock(string sql)
        => sql.Contains("surf_maps", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase);
    private static bool IsGenerationCheck(string sql) => Regex.IsMatch(sql, @"^\s*SELECT\s+1\s+FROM", RegexOptions.IgnoreCase)
                                                        && sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase);

    private static bool IsScoreRead(string sql) => IsCommand(sql, "SELECT", "surf_player_track_scores");

    private sealed class SqlPause : IDisposable
    {
        private readonly StorageServiceImpl _store;
        private int _used;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public SqlPause(StorageServiceImpl store, Func<string, bool> predicate)
        {
            _store = store;
            store.Db.Aop.OnLogExecuting = (sql, _) =>
            {
                if (!predicate(sql) || Interlocked.Exchange(ref _used, 1) != 0) return;
                Entered.TrySetResult();
                if (!Release.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("SQL barrier was not released.");
            };
        }
        public void Dispose() { Release.Set(); _store.Db.Aop.OnLogExecuting = null; Release.Dispose(); }
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
                "Review acceptance requires a disposable loopback database with 'test' in its name.");
            var setup = NewStore();
            setup.Db.DbMaintenance.CreateDatabase();
            setup.Init(startScoreRecalcWorker: false);
        }
        public StorageServiceImpl NewStore(bool worker = false)
        {
            var store = new StorageServiceImpl(_type, _connection, NullLogger<StorageServiceImpl>.Instance, worker);
            _stores.Add(store);
            return store;
        }
        public void Dispose() { foreach (var store in _stores) store.Shutdown(); }
    }

    private sealed class DatabaseTheoryAttribute : TheoryAttribute
    {
        public DatabaseTheoryAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }

    private sealed class DatabaseFactAttribute : FactAttribute
    {
        public DatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}
