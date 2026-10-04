using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

// The profile's overall stats: per style, maps and bonuses completed and the records held, out of the server's
// maps and bonuses, and the time played everywhere.
public sealed class PlayerSummaryStorageTests : IDisposable
{
    private readonly string             _path = Path.Combine(Path.GetTempPath(), $"timer-player-summary-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public PlayerSummaryStorageTests()
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
    public async Task CountsCompletionsAndRecordsPerStyle()
    {
        var player = new SteamID(76561198000000001);
        var other  = new SteamID(76561198000000002);

        // surf_a: the other player holds the map record; this one holds its bonus and a stage.
        await _storage.AddPlayerRecord(player, "surf_a", new RecordRequest { Time = 30 });
        await _storage.AddPlayerRecord(other, "surf_a", new RecordRequest { Time = 28 });
        await _storage.AddPlayerRecord(player, "surf_a", new RecordRequest { Track = 1, Time = 12 });
        await _storage.AddPlayerStageRecord(player, "surf_a", new RecordRequest { Stage = 2, Time = 9 });
        await _storage.AddPlayerStageRecord(other, "surf_a", new RecordRequest { Stage = 2, Time = 10 });

        // surf_b on another style: alone there, so it's a record.
        await _storage.AddPlayerRecord(player, "surf_b", new RecordRequest { Style = 1, Time = 40 });

        await _storage.Db.Updateable<MapEntity>().SetColumns(x => x.Bonuses == 2).Where(x => x.File == "surf_a").ExecuteCommandAsync();
        await _storage.UpdatePlayerMapStatsAsync(player, "surf_a", 100);
        await _storage.UpdatePlayerMapStatsAsync(player, "surf_b", 50);

        var summary = await _storage.GetPlayerSummary(player);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.TotalMaps);
        Assert.Equal(2, summary.TotalBonuses);
        Assert.Equal(150f, summary.PlayTime);
        Assert.Equal([new PlayerStyleSummary(0, 1, 1, 0, 1, 1), new PlayerStyleSummary(1, 1, 0, 1, 0, 0)], summary.Styles);

        var nobody = await _storage.GetPlayerSummary(new SteamID(76561198000000003));
        Assert.NotNull(nobody);
        Assert.Empty(nobody.Styles);
        Assert.Equal(0f, nobody.PlayTime);
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
