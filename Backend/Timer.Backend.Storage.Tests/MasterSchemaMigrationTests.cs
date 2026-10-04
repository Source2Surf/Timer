using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class MasterSchemaMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"timer-master-schema-migration-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task MasterMigrationRejectsUnsupportedProviderBeforeCreatingTables()
    {
        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={_path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(storage.MigrateMasterDatabaseAsync);
            Assert.False(storage.Db.DbMaintenance.IsAnyTable("surf_maps", false));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    [Fact]
    public async Task RunDateConversionRequiresExplicitBackupAcknowledgementBeforeOpeningDatabase()
    {
        var storage = new StorageServiceImpl(DbType.MySql,
                                             "Server=127.0.0.1;Database=timer_unused;User ID=unused;Password=unused",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => storage.ConvertMasterRunDateColumnAsync(backupConfirmed: false));
            Assert.Contains("backup", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            storage.Shutdown();
        }
    }

    [Fact]
    public async Task RunDateConversionRejectsTestOnlySqliteWithoutCreatingTables()
    {
        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={_path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            await Assert.ThrowsAsync<NotSupportedException>(
                () => storage.ConvertMasterRunDateColumnAsync(backupConfirmed: true));
            Assert.False(storage.Db.DbMaintenance.IsAnyTable("surf_runs", false));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    [Fact]
    public void UnixMillisecondsHelpersPreserveTheLegacyUtcClockConvention()
    {
        // SQL providers materialize the master DateTime column as Unspecified. It
        // represented a UTC clock value, rather than the backend host's local time.
        var legacyUnspecified = new DateTime(2026, 9, 16, 1, 2, 3, 456, DateTimeKind.Unspecified);
        var expected = new DateTimeOffset(2026, 9, 16, 1, 2, 3, 456, TimeSpan.Zero).ToUnixTimeMilliseconds();

        var milliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(legacyUnspecified);

        Assert.Equal(expected, milliseconds);
        var roundTrip = StorageServiceImpl.FromUnixTimeMilliseconds(milliseconds);
        Assert.Equal(DateTimeKind.Utc, roundTrip.Kind);
        Assert.Equal(DateTime.SpecifyKind(legacyUnspecified, DateTimeKind.Utc), roundTrip);
    }

    [Fact]
    public void RunEntityMapsHistoricalDateColumnToTypedUnixMilliseconds()
    {
        var property = typeof(RunEntity).GetProperty(nameof(RunEntity.DateUnixTimeMilliseconds))!;
        var column = property.GetCustomAttribute<SugarColumn>()!;

        Assert.Equal("Date", column.ColumnName);
        Assert.Equal("bigint", column.ColumnDataType);
        Assert.Null(typeof(RunEntity).GetProperty("Date"));
    }

    [Fact]
    public async Task PopulatedMasterMapKeepsRowsAndBackfillsNewRequiredTotals()
    {
        using var setup = new SqlSugarScope(new ConnectionConfig
        {
            DbType = DbType.Sqlite,
            ConnectionString = $"Data Source={_path};Pooling=False",
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (_, column) =>
                {
                    if (column.IsIdentity) column.DataType = "INTEGER";
                },
            },
        });
        setup.CodeFirst.InitTables(typeof(MasterMapEntity));
        await setup.Insertable(new MasterMapEntity
        {
            File = "surf_legacy", Tier = 4, Stages = 2, BasePot = 500,
            LegacyCustom = "must-survive",
        }).ExecuteCommandAsync();

        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={_path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
            {
                if (column.IsIdentity) column.DataType = "INTEGER";
            };
            storage.Init();
            storage.Init(); // additive schema changes remain safe on a second pass

            var map = await storage.Db.Queryable<MapEntity>().SingleAsync();
            Assert.Equal("surf_legacy", map.File);
            Assert.Equal(4, map.Tier);
            Assert.Equal(2, map.Stages);
            Assert.Equal(500, map.BasePot);
            Assert.Equal(0, map.Bonuses);
            Assert.Equal(0, map.PlayCount);
            Assert.Equal(0, map.TotalPlayTime);
            Assert.True(storage.Db.DbMaintenance.IsAnyColumn("surf_maps", nameof(MasterMapEntity.LegacyCustom), false));
            Assert.False(storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "SubmissionId", false));
            Assert.False(storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "ServerId", false));
            Assert.False(storage.Db.DbMaintenance.IsAnyIndex("idx_surf_runs_submission_unique"));
            Assert.True(storage.Db.DbMaintenance.IsAnyColumn("surf_run_submissions", nameof(RunSubmissionEntity.SubmissionId), false));
            Assert.True(storage.Db.DbMaintenance.IsAnyIndex("idx_surf_run_submissions_submission_unique"));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    [Fact]
    public void NonMasterServerIdSchemaIsRejectedWithoutModifyingIt()
    {
        using var setup = new SqlSugarScope(new ConnectionConfig
        {
            DbType = DbType.Sqlite,
            ConnectionString = $"Data Source={_path};Pooling=False",
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (_, column) =>
                {
                    if (column.IsIdentity) column.DataType = "INTEGER";
                },
            },
        });
        setup.CodeFirst.InitTables(typeof(UnsupportedRunEntity));

        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={_path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => storage.Init());
            Assert.Contains("master SQL schema only", failure.Message);
            Assert.True(storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "ServerId", false));
            Assert.False(storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "SubmissionId", false));
            Assert.False(storage.Db.DbMaintenance.IsAnyTable("surf_maps", false));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    [Fact]
    public void NonMasterRunSubmissionIdSchemaIsRejectedWithoutModifyingIt()
    {
        using var setup = new SqlSugarScope(new ConnectionConfig
        {
            DbType = DbType.Sqlite,
            ConnectionString = $"Data Source={_path};Pooling=False",
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityService = (_, column) =>
                {
                    if (column.IsIdentity) column.DataType = "INTEGER";
                },
            },
        });
        setup.CodeFirst.InitTables(typeof(UnsupportedSubmissionRunEntity));

        var storage = new StorageServiceImpl(DbType.Sqlite,
                                             $"Data Source={_path};Pooling=False",
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            var failure = Assert.Throws<InvalidOperationException>(() => storage.Init());
            Assert.Contains("master SQL schema only", failure.Message);
            Assert.True(storage.Db.DbMaintenance.IsAnyColumn("surf_runs", "SubmissionId", false));
            Assert.False(storage.Db.DbMaintenance.IsAnyTable("surf_maps", false));
        }
        finally
        {
            storage.Shutdown();
        }
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [SugarTable("surf_maps")]
    private sealed class MasterMapEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public ulong MapId { get; set; }

        public string File { get; set; } = string.Empty;

        public byte Tier { get; set; }

        public ushort Stages { get; set; }

        public int BasePot { get; set; }

        [SugarColumn(Length = 64)]
        public string LegacyCustom { get; set; } = string.Empty;
    }

    [SugarTable("surf_runs")]
    private sealed class UnsupportedRunEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public ulong Id { get; set; }

        [SugarColumn(Length = 64)]
        public string ServerId { get; set; } = string.Empty;
    }

    [SugarTable("surf_runs")]
    private sealed class UnsupportedSubmissionRunEntity
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
        public ulong Id { get; set; }

        [SugarColumn(Length = 32)]
        public string SubmissionId { get; set; } = string.Empty;
    }
}
