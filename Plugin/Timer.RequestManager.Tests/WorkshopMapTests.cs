using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class WorkshopMapTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-workshop-maps-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _server;
    private readonly StorageServiceImpl _backend;

    public WorkshopMapTests()
    {
        var connectionString = $"Data Source={_path};Pooling=False";
        _server = NewStorage(connectionString);
        _backend = NewStorage(connectionString);
        _server.Init();
        _backend.Init(initializeSchema: false);
    }

    [Fact]
    public async Task NewWorkshopMapStoresItsItem()
    {
        _server.SetMapWorkshopId("surf_a", 123);
        var map = await _server.GetMapInfo("surf_a");

        Assert.Equal(123ul, map.WorkshopId);
        Assert.Equal(123ul, (await GetRowAsync(map.MapId)).WorkshopId);
    }

    [Fact]
    public async Task MapStoredByNameIsClaimedByItsItem()
    {
        var legacy = await _server.GetMapInfo("surf_a");

        _server.SetMapWorkshopId("surf_a", 123);
        var map = await _server.GetMapInfo("surf_a");

        Assert.Equal(legacy.MapId, map.MapId);
        Assert.Equal(123ul, (await GetRowAsync(map.MapId)).WorkshopId);
    }

    [Fact]
    public async Task RenamedWorkshopMapKeepsItsRow()
    {
        _server.SetMapWorkshopId("surf_a_v1", 123);
        var v1 = await _server.GetMapInfo("surf_a_v1");
        v1.Tier[0] = 4;
        await _server.UpdateMapInfo(v1);

        _server.SetMapWorkshopId("surf_a_v2", 123);
        var v2 = await _server.GetMapInfo("surf_a_v2");

        Assert.Equal(v1.MapId, v2.MapId);
        Assert.Equal("surf_a_v2", v2.MapName);
        Assert.Equal(4, v2.Tier[0]);
        Assert.Null(await _server.ResolveMapIdByNameAsync("surf_a_v1"));
        // The backend only knows names, and must land on the same row.
        Assert.Equal(v1.MapId, await _backend.ResolveMapIdByNameAsync("surf_a_v2"));
    }

    [Fact]
    public async Task RenameOntoATakenNameLeavesBothRows()
    {
        _server.SetMapWorkshopId("surf_old", 123);
        var old = await _server.GetMapInfo("surf_old");
        _server.SetMapWorkshopId("surf_new", 0);
        var taken = await _server.GetMapInfo("surf_new");

        _server.SetMapWorkshopId("surf_new", 123);
        var map = await _server.GetMapInfo("surf_new");

        Assert.Equal(taken.MapId, map.MapId);
        Assert.Equal("surf_old", (await GetRowAsync(old.MapId)).File);
        Assert.Equal(0ul, (await GetRowAsync(taken.MapId)).WorkshopId);
    }

    [Fact]
    public async Task MapMovesToANewItemOfTheSameName()
    {
        _server.SetMapWorkshopId("surf_x", 111);
        var first = await _server.GetMapInfo("surf_x");

        _server.SetMapWorkshopId("surf_x", 222);
        var map = await _server.GetMapInfo("surf_x");

        Assert.Equal(first.MapId, map.MapId);
        Assert.Equal(222ul, (await GetRowAsync(map.MapId)).WorkshopId);
    }

    [Fact]
    public async Task OtherMapsStillResolveByName()
    {
        var other = await _server.GetMapInfo("surf_other");
        _server.SetMapWorkshopId("surf_a", 123);

        Assert.Equal(other.MapId, await _server.ResolveMapIdByNameAsync("surf_other"));
        Assert.Equal(0ul, (await GetRowAsync(other.MapId)).WorkshopId);
    }

    public void Dispose()
    {
        _server.Shutdown();
        _backend.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }

    private Task<MapEntity> GetRowAsync(ulong mapId)
        => _server.Db.Queryable<MapEntity>().Where(x => x.MapId == mapId).SingleAsync();

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
