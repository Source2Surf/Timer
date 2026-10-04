using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

// The replay menu's My runs: one player's PB history on one leaderboard, newest first.
public sealed class PlayerRunsStorageTests : IDisposable
{
    private const string Map = "surf_player_runs";

    private readonly string             _path = Path.Combine(Path.GetTempPath(), $"timer-player-runs-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public PlayerRunsStorageTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite,
                                          $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
    }

    [Fact]
    public Task ListsThatLeaderboardsPersonalBestsNewestFirst()
        => PersonalBestsNewestFirst(_storage, Map, new SteamID(76561198000000001), new SteamID(76561198000000002));

    [DatabaseFact("TIMER_TEST_MYSQL")]
    public Task ListsPersonalBestsOnMySql() => OnDatabase(DbType.MySql, "TIMER_TEST_MYSQL");

    [DatabaseFact("TIMER_TEST_POSTGRES")]
    public Task ListsPersonalBestsOnPostgreSql() => OnDatabase(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task OnDatabase(DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        var parsed     = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connection };
        Assert.True(Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]) is "127.0.0.1" or "localhost" or "::1"
                    && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase));

        var storage = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, enableScoreRecalcWorker: false);
        try
        {
            storage.Db.DbMaintenance.CreateDatabase();
            storage.Init(startScoreRecalcWorker: false);
            var player = new SteamID(76561198000000000UL + (ulong) Random.Shared.NextInt64(1, 1_000_000_000));
            await PersonalBestsNewestFirst(storage, $"surf_runs_{Guid.NewGuid():N}", player, new SteamID(player.AsPrimitive() + 1));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    private static async Task PersonalBestsNewestFirst(StorageServiceImpl storage, string map, SteamID player, SteamID other)
    {
        foreach (var time in new[] { 40f, 35f, 38f, 35f })
        {
            await storage.AddPlayerRecord(player, map, new RecordRequest { Time = time });
        }

        await storage.AddPlayerRecord(player, map, new RecordRequest { Style = 1, Time = 50 });
        await storage.AddPlayerRecord(player, map, new RecordRequest { Track = 1, Time = 12 });
        await storage.AddPlayerRecord(other, map, new RecordRequest { Time = 30 });
        await storage.AddPlayerStageRecord(player, map, new RecordRequest { Stage = 2, Time = 9 });
        await storage.AddPlayerStageRecord(player, map, new RecordRequest { Stage = 2, Time = 11 });

        // 38 and the second 35 didn't beat their PB when they were set.
        var main = await storage.GetPlayerRuns(map, player, 0, 0, 0);
        Assert.Equal([35f, 40f], main.Select(r => r.Time));
        Assert.All(main, r => Assert.Equal((ulong) player.AsPrimitive(), r.SteamId));

        var stage = await storage.GetPlayerRuns(map, player, 0, 0, 2);
        Assert.Equal([9f], stage.Select(r => r.Time));
        Assert.All(stage, r => Assert.Equal(2, r.Stage));

        Assert.Single(await storage.GetPlayerRuns(map, player, 1, 0, 0));
        Assert.Single(await storage.GetPlayerRuns(map, player, 0, 1, 0));
        Assert.Single(await storage.GetPlayerRuns(map, player, 0, 0, 0, limit: 1));
        // The profile's recent runs still list every finish.
        Assert.Equal(6, (await storage.GetRecentRecords(map, player)).Count);
        Assert.Empty(await storage.GetPlayerRuns("surf_unknown", player, 0, 0, 0));
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
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
