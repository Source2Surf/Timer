using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class MapStatsAtomicityTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-map-stats-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _first;
    private readonly StorageServiceImpl _second;

    public MapStatsAtomicityTests()
    {
        var connectionString = $"Data Source={_path};Pooling=False";
        _first = NewStorage(connectionString);
        _second = NewStorage(connectionString);
        _first.Init();
        _second.Init(initializeSchema: false);
    }

    [Fact]
    public async Task ConcurrentServerSessionsAddRatherThanOverwriteAnOldProfile()
    {
        var map = await _first.GetMapInfo($"surf_stats_shared_{Guid.NewGuid():N}");
        var oldProfile = await _second.GetMapInfo(map.MapName);

        await Task.WhenAll(_first.IncrementMapStatsAsync(map.MapName, 10f),
                           _second.IncrementMapStatsAsync(map.MapName, 20f));

        oldProfile.Tier[0] = 3;
        await _second.UpdateMapInfo(oldProfile);

        var saved = await _first.Db.Queryable<MapEntity>()
                                   .Where(x => x.MapId == map.MapId)
                                   .SingleAsync();
        Assert.Equal(2, saved.PlayCount);
        Assert.Equal(30f, saved.TotalPlayTime);
        Assert.Equal(3, saved.Tier);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-1f)]
    public async Task InvalidSessionDeltaDoesNotIncrementCounters(float delta)
    {
        var map = await _first.GetMapInfo($"surf_stats_invalid_{Guid.NewGuid():N}");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _first.IncrementMapStatsAsync(map.MapName, delta));

        var saved = await _first.Db.Queryable<MapEntity>()
                                   .Where(x => x.MapId == map.MapId)
                                   .SingleAsync();
        Assert.Equal(0, saved.PlayCount);
        Assert.Equal(0f, saved.TotalPlayTime);
    }

    public void Dispose()
    {
        _first.Shutdown();
        _second.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static StorageServiceImpl NewStorage(string connectionString)
    {
        var storage = new StorageServiceImpl(DbType.Sqlite, connectionString,
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        return storage;
    }
}
