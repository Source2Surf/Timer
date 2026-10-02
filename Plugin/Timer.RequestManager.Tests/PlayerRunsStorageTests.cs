using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

// The replay menu's My runs: one player's finishes on one leaderboard, slower ones included, newest first.
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
    public async Task ListsOnlyThatLeaderboardsRunsNewestFirst()
    {
        var player = new SteamID(76561198000000001);
        var other  = new SteamID(76561198000000002);

        foreach (var time in new[] { 40f, 35f, 38f })
        {
            await _storage.AddPlayerRecord(player, Map, new RecordRequest { Time = time });
        }

        await _storage.AddPlayerRecord(player, Map, new RecordRequest { Style = 1, Time = 50 });
        await _storage.AddPlayerRecord(player, Map, new RecordRequest { Track = 1, Time = 12 });
        await _storage.AddPlayerRecord(other, Map, new RecordRequest { Time = 30 });
        await _storage.AddPlayerStageRecord(player, Map, new RecordRequest { Stage = 2, Time = 9 });
        await _storage.AddPlayerStageRecord(player, Map, new RecordRequest { Stage = 2, Time = 11 });

        var main = await _storage.GetPlayerRuns(Map, player, 0, 0, 0);
        Assert.Equal([38f, 35f, 40f], main.Select(r => r.Time));
        Assert.All(main, r => Assert.Equal((ulong) player.AsPrimitive(), r.SteamId));

        var stage = await _storage.GetPlayerRuns(Map, player, 0, 0, 2);
        Assert.Equal([11f, 9f], stage.Select(r => r.Time));
        Assert.All(stage, r => Assert.Equal(2, r.Stage));

        Assert.Single(await _storage.GetPlayerRuns(Map, player, 1, 0, 0));
        Assert.Single(await _storage.GetPlayerRuns(Map, player, 0, 1, 0));
        Assert.Equal(2, (await _storage.GetPlayerRuns(Map, player, 0, 0, 0, limit: 2)).Count);
        Assert.Empty(await _storage.GetPlayerRuns("surf_unknown", player, 0, 0, 0));
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
