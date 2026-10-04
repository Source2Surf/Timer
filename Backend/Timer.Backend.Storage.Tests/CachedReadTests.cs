using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class CachedReadTests
{
    private const ulong Sentinel = 9_100_000_000_000_001;

    [Fact]
    public void ArgumentsBindToTheParametersThatHeldTheirSentinels()
    {
        var read = CachedRead.Create(new("SELECT 1 WHERE a = @a AND b = @b AND c = @c",
                                         [new("@a", Sentinel), new("@b", 3), new("@c", Sentinel)]),
                                     [Sentinel]);

        var bound = read.Bind([42UL]);

        Assert.Equal([42UL, 3, 42UL], bound.Select(x => x.Value));
    }

    [Fact]
    public void AnArgumentWrittenIntoTheSqlIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            CachedRead.Create(new($"SELECT 1 WHERE a = {Sentinel}", []), [Sentinel]));

    [Fact]
    public void AnArgumentNoParameterHoldsIsRejected()
        => Assert.Throws<InvalidOperationException>(() =>
            CachedRead.Create(new("SELECT 1 WHERE a = @a", [new("@a", 5)]), [Sentinel]));

    [Fact]
    public Task SqliteReadsReuseTheirShapesWithNewValues() => RunSuite(DbType.Sqlite, null);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlReadsReuseTheirShapesWithNewValues() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlReadsReuseTheirShapesWithNewValues() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    // Every read alternates between maps, players and runs, so a shape that kept its first values would show.
    private static async Task RunSuite(DbType type, string? variable)
    {
        var path       = Path.Combine(Path.GetTempPath(), $"timer-cached-read-{Guid.NewGuid():N}.db");
        var connection = type == DbType.Sqlite ? $"Data Source={path};Pooling=False" : Environment.GetEnvironmentVariable(variable!)!;
        if (type != DbType.Sqlite)
        {
            var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
            Assert.Contains("test", Convert.ToString(parsed["Database"])!, StringComparison.OrdinalIgnoreCase);
        }

        var store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        if (type == DbType.Sqlite)
        {
            store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
            {
                if (column.IsIdentity) column.DataType = "INTEGER";
            };
        }
        else
        {
            store.Db.DbMaintenance.CreateDatabase();
        }

        try
        {
            store.Init(startScoreRecalcWorker: false);
            await Exercise(store);
        }
        finally
        {
            store.Shutdown();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task Exercise(StorageServiceImpl store)
    {
        var a = $"surf_cached_{Guid.NewGuid():N}";
        var b = $"surf_cached_{Guid.NewGuid():N}";
        var p = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 500_000_000));
        var q = new SteamID(p.AsPrimitive() + 1);

        await store.GetPlayerProfile(p, "Alpha");
        await store.GetPlayerProfile(q, "Beta");
        var (_, pa, _) = await store.AddPlayerRecord(p, a, Run(60, 1));
        await store.AddPlayerRecord(q, a, Run(70, 0));
        var (_, qb, _) = await store.AddPlayerRecord(q, b, Run(50, 2));
        await store.AddPlayerStageRecord(p, a, new RecordRequest { Stage = 1, Time = 10 });

        for (var round = 0; round < 2; round++)
        {
            Assert.Equal([(p.AsPrimitive(), "Alpha", 60f), (q.AsPrimitive(), "Beta", 70f)],
                         (await store.GetMapRecords(a, 0, 0)).Select(x => (x.SteamId, x.PlayerName, x.Time)));
            Assert.Equal([(q.AsPrimitive(), 50f)], (await store.GetMapRecords(b, 0, 0)).Select(x => (x.SteamId, x.Time)));
            Assert.Equal(2, (await store.GetMapRecords(a)).Count);
            Assert.Equal(1, Assert.Single(await store.GetMapStageRecords(a)).Stage);
            Assert.Empty(await store.GetMapStageRecords(b));

            Assert.Equal(60f, Assert.Single(await store.GetPlayerRecords(p, a)).Time);
            Assert.Equal(70f, Assert.Single(await store.GetPlayerRecords(q, a)).Time);
            Assert.Equal(50f, Assert.Single(await store.GetPlayerRecords(q, b)).Time);
            Assert.Empty(await store.GetPlayerRecords(p, b));
            Assert.Equal(10f, Assert.Single(await store.GetPlayerStageRecords(p, a)).Time);

            Assert.Equal(1, (await store.GetRecordCheckpoints(pa.Id)).Count);
            Assert.Equal([1u, 2u], (await store.GetRecordCheckpoints(qb.Id)).Select(x => x.CheckpointIndex));
        }

        await store.UpdatePlayerMapStatsAsync(p, a, 5);
        await store.UpdatePlayerMapStatsAsync(q, a, 7);
        Assert.Equal((5f, 1), await store.GetPlayerMapStatsAsync(p, a));
        Assert.Equal((7f, 1), await store.GetPlayerMapStatsAsync(q, a));
        Assert.Equal((0f, 0), await store.GetPlayerMapStatsAsync(p, b));

        foreach (var (player, points) in new[] { (p, 4_000_000_000u), (q, 3_999_999_999u) })
        {
            var id = unchecked((long)player.AsPrimitive());
            await store.Db.Updateable<PlayerEntity>().SetColumns(x => x.Points == points).Where(x => x.SteamId == id).ExecuteCommandAsync();
        }

        var (rankP, totalP) = await store.GetPlayerPointsRank(p);
        var (rankQ, totalQ) = await store.GetPlayerPointsRank(q);
        Assert.Equal(rankP + 1, rankQ);
        Assert.Equal(totalP, totalQ);
    }

    private static RecordRequest Run(float time, int checkpoints)
    {
        var run = new RecordRequest { Time = time };
        for (var i = 1; i <= checkpoints; i++)
        {
            run.Checkpoints.Add(new RecordRequest.CheckpointRecord { CheckpointIndex = i, Time = time * i / (checkpoints + 1) });
        }

        return run;
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
