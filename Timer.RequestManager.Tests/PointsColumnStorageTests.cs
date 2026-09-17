using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class PointsColumnStorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-points-columns-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public PointsColumnStorageTests()
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
    public async Task FreshSqliteSchemaUses64BitPointsAffinityAndRoundTripsTheEntireUintRange()
    {
        AssertBigIntMapping(typeof(PlayerEntity));
        AssertBigIntMapping(typeof(PlayerTrackScoreEntity));

        const long steamId = 76561198000000001;
        await _storage.Db.Insertable(new PlayerEntity
        {
            SteamId = steamId,
            Name = "SQLite BIGINT points",
            Points = uint.MaxValue,
            Runs = 1,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        await _storage.Db.Insertable(new PlayerTrackScoreEntity
        {
            SteamId = steamId,
            MapId = 1,
            Style = 0,
            Track = 0,
            Points = uint.MaxValue,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        Assert.Equal(uint.MaxValue, await _storage.Db.Queryable<PlayerEntity>()
                                                .Select(row => row.Points)
                                                .SingleAsync());
        Assert.Equal(uint.MaxValue, await _storage.Db.Queryable<PlayerTrackScoreEntity>()
                                                .Select(row => row.Points)
                                                .SingleAsync());

        AssertSqlite64BitIntegerAffinity("surf_players");
        AssertSqlite64BitIntegerAffinity("surf_player_track_scores");
    }

    [Fact]
    public async Task PointsWideningRefusesTestOnlySqliteWithoutTouchingTheSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"timer-points-migration-unsupported-{Guid.NewGuid():N}.db");
        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(storage.MigrateMasterPointsColumnsAsync);
            Assert.False(storage.Db.DbMaintenance.IsAnyTable("surf_players", false));
        }
        finally
        {
            storage.Shutdown();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void AssertBigIntMapping(Type entityType)
    {
        var property = entityType.GetProperty(nameof(PlayerEntity.Points))!;
        var column = property.GetCustomAttribute<SugarColumn>()!;
        Assert.Equal("bigint", column.ColumnDataType);
        Assert.Equal(typeof(UInt32BigIntConverter), column.SqlParameterDbType);
    }

    private void AssertSqlite64BitIntegerAffinity(string tableName)
    {
        var column = _storage.Db.DbMaintenance.GetColumnInfosByTableName(tableName, false)
                             .Single(info => string.Equals(info.DbColumnName, nameof(PlayerEntity.Points), StringComparison.OrdinalIgnoreCase));
        var type = column.DataType.Trim().ToLowerInvariant();
        Assert.True(type is "bigint" or "integer",
                    $"SQLite should expose the BIGINT mapping as a 64-bit integer affinity, not '{column.DataType}'.");
        Assert.False(column.IsNullable);
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
