using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class RunDeletionTests
{
    private static readonly Dictionary<int, double> Factors = new() { [0] = 1 };

    [Fact]
    public Task SqliteDeletesARunAndPromotesTheNextBest() => RunSuite(DbType.Sqlite, null);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlDeletesARunAndPromotesTheNextBest() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlDeletesARunAndPromotesTheNextBest() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task RunSuite(DbType type, string? variable)
    {
        var path       = Path.Combine(Path.GetTempPath(), $"timer-run-delete-{Guid.NewGuid():N}.db");
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
        var map = $"surf_delete_{Guid.NewGuid():N}";
        var other = $"surf_delete_{Guid.NewGuid():N}";
        var p = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 500_000_000));
        var q = new SteamID(p.AsPrimitive() + 1);
        await store.GetPlayerProfile(p, "Two runs");
        await store.GetPlayerProfile(q, "One run");

        var (_, slow, _) = await store.AddPlayerRecord(p, map, Run(70.125f));
        var (_, fast, _) = await store.AddPlayerRecord(p, map, Run(60.375f));
        var (_, only, _) = await store.AddPlayerRecord(q, map, Run(65.5f));
        var (_, stage, _) = await store.AddPlayerStageRecord(p, map, new RecordRequest { Stage = 1, Time = 10 });
        await store.SaveReplayUrlAsync(map, p.AsPrimitive(), (ulong)fast.Id, "https://test.invalid/fast");
        var mapId = (await store.GetMapInfo(map)).MapId;

        // Neither a run of another map nor an unknown one.
        Assert.Null(await store.DeleteRunAsync(other, (ulong)fast.Id, Factors));
        Assert.Null(await store.DeleteRunAsync(map, ulong.MaxValue - 1, Factors));
        Assert.Null(await store.DeleteRunAsync(map, long.MaxValue, Factors));

        // A slower run: the board stays as it was, nothing is requeued.
        var generation = await Generation(store, mapId);
        var deleted = Assert.IsType<TimerBackendDeletedRun>(await store.DeleteRunAsync(map, (ulong)slow.Id, Factors));
        Assert.False(deleted.WasBest);
        Assert.Equal(generation, await Generation(store, mapId));
        Assert.Equal([60.375f, 65.5f], (await store.GetMapRecords(map, 0, 0)).Select(x => x.Time));

        // Their best, with a replay and checkpoints: it goes, and their next run takes its place.
        await store.AddPlayerRecord(p, map, Run(80.25f));
        deleted = Assert.IsType<TimerBackendDeletedRun>(await store.DeleteRunAsync(map, (ulong)fast.Id, Factors));
        Assert.True(deleted.WasBest);
        Assert.Equal(p.AsPrimitive(), deleted.SteamId);
        Assert.False(deleted.StageRun);
        Assert.Equal(["https://test.invalid/fast"], deleted.ReplayUrls);
        Assert.Equal(generation + 1, await Generation(store, mapId));
        Assert.Equal([(q.AsPrimitive(), 65.5f), (p.AsPrimitive(), 80.25f)],
                     (await store.GetMapRecords(map, 0, 0)).Select(x => (x.SteamId, x.Time)));
        Assert.Empty(await store.GetRecordCheckpoints(fast.Id));
        Assert.Null(await store.GetRunReplayUrlAsync((ulong)fast.Id));
        // The promoted best holds the run's own stored time, not a rounded copy.
        Assert.True(await store.Db.Queryable<PlayerBestRunEntity>()
                               .InnerJoin<RunEntity>((best, run) => best.RunId == run.Id)
                               .Where((best, run) => best.MapId == mapId && best.SteamId == unchecked((long)p.AsPrimitive())
                                                     && best.RunType == RunType.Main && best.BestTime == run.Time)
                               .AnyAsync());

        // The only run of a player: they leave the board, and the requeued scores drop them.
        await Recalculate(store, mapId);
        Assert.True((await Profile(store, q)).Points > 0);
        deleted = Assert.IsType<TimerBackendDeletedRun>(await store.DeleteRunAsync(map, (ulong)only.Id, Factors));
        Assert.True(deleted.WasBest);
        Assert.Empty(deleted.ReplayUrls);
        Assert.Equal(generation + 2, await Generation(store, mapId));
        await Recalculate(store, mapId);
        Assert.Equal([p.AsPrimitive()], (await store.GetMapRecords(map, 0, 0)).Select(x => x.SteamId));
        Assert.Equal(0u, (await Profile(store, q)).Points);
        Assert.Equal(0, await store.Db.Queryable<PlayerTrackScoreEntity>()
                                   .Where(x => x.SteamId == unchecked((long)q.AsPrimitive())).CountAsync());

        // A stage run leaves the main board's scores alone.
        generation = await Generation(store, mapId);
        deleted = Assert.IsType<TimerBackendDeletedRun>(await store.DeleteRunAsync(map, (ulong)stage.Id, Factors));
        Assert.True(deleted.StageRun);
        Assert.Equal(1, deleted.Stage);
        Assert.Equal(generation, await Generation(store, mapId));
        Assert.Empty(await store.GetMapStageRecords(map));

        // Gone already.
        Assert.Null(await store.DeleteRunAsync(map, (ulong)stage.Id, Factors));
    }

    private static RecordRequest Run(float time)
    {
        var run = new RecordRequest { Time = time };
        run.Checkpoints.Add(new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = time / 2 });
        return run;
    }

    private static async Task<long> Generation(StorageServiceImpl store, ulong mapId)
        => (await store.Db.Queryable<ScoreRecalcOutboxEntity>()
                       .Where(x => x.MapId == mapId && x.Style == 0 && x.Track == 0)
                       .FirstAsync())?.RequestedGeneration ?? 0;

    // Only this board: draining the outbox would take other tests' work from a shared database.
    private static Task Recalculate(StorageServiceImpl store, ulong mapId)
        => store.RecalculateTrackScoresAsync(mapId, 0, 0, 1);

    private static Task<PlayerEntity> Profile(StorageServiceImpl store, SteamID player)
    {
        var id = unchecked((long)player.AsPrimitive());

        return store.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == id).FirstAsync();
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
