using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class RunDateStorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-run-date-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public RunDateStorageTests()
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
    public async Task CurrentRunWritesBigIntUnixMillisecondsAndReadsBackUtc()
    {
        var player = new SteamID(76561198000000001);
        var (_, record, _) = await _storage.AddPlayerRecord(player,
                                                              "surf_unix_date",
                                                              new RecordRequest { Time = 42 });

        var stored = await _storage.Db.Queryable<RunEntity>().SingleAsync();
        Assert.Equal(StorageServiceImpl.ToUnixTimeMilliseconds(record.RunDate),
                     stored.DateUnixTimeMilliseconds);
        Assert.Equal(StorageServiceImpl.FromUnixTimeMilliseconds(stored.DateUnixTimeMilliseconds),
                     record.RunDate);
        Assert.Equal(DateTimeKind.Utc, record.RunDate.Kind);

        var recent = Assert.Single(await _storage.GetRecentRecords("surf_unix_date", player));
        Assert.Equal(record.RunDate, recent.RunDate);
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
