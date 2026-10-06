using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class PlayerWipeTests
{
    private static readonly Dictionary<int, double> Factors = new() { [0] = 1 };

    [Fact]
    public Task SqliteWipesEveryRunOfAPlayer() => RunSuite(DbType.Sqlite, null);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlWipesEveryRunOfAPlayer() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlWipesEveryRunOfAPlayer() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task RunSuite(DbType type, string? variable)
    {
        var path       = Path.Combine(Path.GetTempPath(), $"timer-player-wipe-{Guid.NewGuid():N}.db");
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
        var first  = $"surf_wipe_{Guid.NewGuid():N}";
        var second = $"surf_wipe_{Guid.NewGuid():N}";
        var p      = new SteamID(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 500_000_000));
        var q      = new SteamID(p.AsPrimitive() + 1);
        await store.GetPlayerProfile(p, "Cheater");
        await store.GetPlayerProfile(q, "Legit");

        var (_, slow, _)  = await store.AddPlayerRecord(p, first, new RecordRequest { Time = 70.125f });
        var (_, fast, _)  = await store.AddPlayerRecord(p, first, new RecordRequest { Time = 30.25f });
        var (_, stage, _) = await store.AddPlayerStageRecord(p, first, new RecordRequest { Stage = 1, Time = 10 });
        var (_, other, _) = await store.AddPlayerRecord(p, second, new RecordRequest { Time = 40.5f });
        var (_, legit, _) = await store.AddPlayerRecord(q, first, new RecordRequest { Time = 65.5f });
        await store.SaveReplayUrlAsync(first, p.AsPrimitive(), (ulong)fast.Id, "https://test.invalid/fast");
        var firstId  = (await store.GetMapInfo(first)).MapId;
        var secondId = (await store.GetMapInfo(second)).MapId;

        await store.RecalculateTrackScoresAsync(firstId, 0, 0, 1);
        await store.RecalculateTrackScoresAsync(secondId, 0, 0, 1);
        Assert.True((await Profile(store, p)).Points > 0);

        // A dry run counts and leaves everything.
        var counted = await store.WipePlayerRunsAsync(p, true, Factors);
        Assert.Equal((2, 4), (counted.Maps, counted.Runs));
        Assert.Empty(counted.Deleted);
        Assert.Equal(2, (await store.GetMapRecords(first, 0, 0)).Count);

        var generations = (await Generation(store, firstId), await Generation(store, secondId));
        var wiped = await store.WipePlayerRunsAsync(p, false, Factors);

        Assert.Equal((2, 4), (wiped.Maps, wiped.Runs));
        Assert.Equal(new[] { slow.Id, fast.Id, stage.Id, other.Id }.Order(), wiped.Deleted.Select(x => (long)x.Run.RunId).Order());
        Assert.All(wiped.Deleted, x => Assert.Equal(p.AsPrimitive(), x.Run.SteamId));
        var byId = wiped.Deleted.ToDictionary(x => (long)x.Run.RunId);
        Assert.Equal((first, 30.25f, true), (byId[fast.Id].MapName, byId[fast.Id].Time, byId[fast.Id].Run.WasBest));
        Assert.Equal(["https://test.invalid/fast"], byId[fast.Id].Run.ReplayUrls);
        Assert.False(byId[slow.Id].Run.WasBest);
        Assert.True(byId[stage.Id].Run is { StageRun: true, Stage: 1, WasBest: true });
        Assert.Equal((second, true), (byId[other.Id].MapName, byId[other.Id].Run.WasBest));

        // Only the other player is left, and both main boards are requeued.
        Assert.Equal([q.AsPrimitive()], (await store.GetMapRecords(first, 0, 0)).Select(x => x.SteamId));
        Assert.Empty(await store.GetMapRecords(second, 0, 0));
        Assert.Empty(await store.GetMapStageRecords(first));
        Assert.Null(await store.GetRunReplayUrlAsync((ulong)fast.Id));
        Assert.Empty(await store.GetRecordCheckpoints(fast.Id));
        var id = unchecked((long)p.AsPrimitive());
        Assert.False(await store.Db.Queryable<RunEntity>().Where(x => x.SteamId == id).AnyAsync());
        Assert.False(await store.Db.Queryable<PlayerBestRunEntity>().Where(x => x.SteamId == id).AnyAsync());
        Assert.Equal((generations.Item1 + 1, generations.Item2 + 1), (await Generation(store, firstId), await Generation(store, secondId)));

        // The requeued scores drop their points and keep the other player's.
        await store.RecalculateTrackScoresAsync(firstId, 0, 0, 1);
        await store.RecalculateTrackScoresAsync(secondId, 0, 0, 1);
        Assert.Equal(0u, (await Profile(store, p)).Points);
        Assert.False(await store.Db.Queryable<PlayerTrackScoreEntity>().Where(x => x.SteamId == id).AnyAsync());
        Assert.True((await Profile(store, q)).Points > 0);
        Assert.Equal(legit.Id, (await store.GetMapRecords(first, 0, 0)).Single().Id);

        // Nothing is left to wipe.
        var again = await store.WipePlayerRunsAsync(p, false, Factors);
        Assert.Equal((0, 0), (again.Maps, again.Runs));
    }

    private static async Task<long> Generation(StorageServiceImpl store, ulong mapId)
        => (await store.Db.Queryable<ScoreRecalcOutboxEntity>()
                       .Where(x => x.MapId == mapId && x.Style == 0 && x.Track == 0)
                       .FirstAsync())?.RequestedGeneration ?? 0;

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
