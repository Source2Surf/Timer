using System.Data.Common;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Timer.RequestManager.Tests;

/// <summary>
/// Opt-in, destructive only to an explicitly designated disposable SQL database.
/// It deliberately creates the physical schema represented by <c>master</c>, invokes
/// the real backend CLI twice, and then verifies the resulting backend schema through
/// typed SQLSugar metadata and queries.  It must never be pointed at an operator DB.
/// </summary>
[Collection(SqlSchemaMutationCollection.Name)]
public sealed class MasterSqlMigrationAcceptanceTests(ITestOutputHelper output)
{
    private const string MySqlConnectionEnvironment = "TIMER_TEST_MYSQL";
    private const string PostgreSqlConnectionEnvironment = "TIMER_TEST_POSTGRES";
    private const string MySqlEnableEnvironment = "TIMER_TEST_MASTER_MIGRATION";
    private const string PostgreSqlEnableEnvironment = "TIMER_TEST_POSTGRES_MASTER_MIGRATION";
    private const string MasterMapName = "surf_master_sql_acceptance";

    [MasterSqlMigrationFact(MySqlConnectionEnvironment, MySqlEnableEnvironment)]
    public Task MySqlMasterSchemaConvertsDateThenMigratesThroughTheActualBackendCli()
        => MasterSchemaConvertsDateThenMigratesThroughTheActualBackendCli(
            DbType.MySql, MySqlConnectionEnvironment, MySqlEnableEnvironment);

    [MasterSqlMigrationFact(PostgreSqlConnectionEnvironment, PostgreSqlEnableEnvironment)]
    public Task PostgreSqlMasterSchemaConvertsDateThenMigratesThroughTheActualBackendCli()
        => MasterSchemaConvertsDateThenMigratesThroughTheActualBackendCli(
            DbType.PostgreSQL, PostgreSqlConnectionEnvironment, PostgreSqlEnableEnvironment);

    private async Task MasterSchemaConvertsDateThenMigratesThroughTheActualBackendCli(
        DbType databaseType,
        string connectionEnvironment,
        string enableEnvironment)
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionEnvironment)!;
        EnsureDisposableLoopbackDatabase(connectionString, databaseType, enableEnvironment);

        using var db = CreateDatabase(databaseType, connectionString);
        await RecreatePublishedMasterSchemaAsync(db, enableEnvironment);

        var seeded = await SeedRepresentativeMasterRowsAsync(db);
        var originalMapCount = await db.Queryable<MasterMapRow>().CountAsync();
        var originalRunCount = await db.Queryable<MasterRunRow>().CountAsync();
        var originalRunColumns = DescribeColumns(db, "surf_runs");
        var originalRunIndexes = DescribeIndexes(db, "surf_runs");
        var originalScoreIndexes = DescribeIndexes(db, "surf_player_track_scores");
        var legacyDateColumn = GetColumn(db, "surf_runs", "Date");
        Assert.True(IsTemporal(legacyDateColumn.DataType),
                    $"Expected the master Date column to be temporal, got '{legacyDateColumn.DataType}'.");

        await RunBackendCliAsync(databaseType, "convert-run-dates", "--backup-confirmed", connectionString);

        var convertedDateColumn = GetColumn(db, "surf_runs", "Date");
        Assert.True(IsBigInt(convertedDateColumn.DataType),
                    $"Expected surf_runs.Date to be BIGINT, got '{convertedDateColumn.DataType}'.");
        Assert.False(convertedDateColumn.IsNullable);
        Assert.False(db.DbMaintenance.IsAnyColumn("surf_runs", "date_unix_milliseconds_migration", false));
        Assert.False(db.DbMaintenance.IsAnyColumn("surf_runs", "date_datetime_migration_backup", false));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_surf_runs_recent_main"));

        var convertedRows = await db.Queryable<RunEntity>()
                                    .Where(row => row.MapId == seeded.MapId)
                                    .OrderBy(row => row.Style)
                                    .ToListAsync();
        Assert.Equal(seeded.ExpectedDates.Length, convertedRows.Count);
        Assert.Equal(seeded.ExpectedDates, convertedRows.Select(row => row.DateUnixTimeMilliseconds).ToArray());

        // The conversion calls CodeFirst for this index because its strongly typed
        // metadata is the only SQLSugar API that retains per-column DESC ordering.
        // Recreate it once on the disposable database while recording generated SQL
        // so the contract is verified without hand-written DDL or SHOW/EXPLAIN SQL.
        Assert.True(db.DbMaintenance.DropIndex("idx_surf_runs_recent_main", "surf_runs"));
        var generatedDdl = new List<string>();
        db.Aop.OnLogExecuting = (sql, _) => generatedDdl.Add(sql);
        try
        {
            db.CodeFirst.InitTables(typeof(RunEntity));
        }
        finally
        {
            db.Aop.OnLogExecuting = null;
        }

        Assert.True(db.DbMaintenance.IsAnyIndex("idx_surf_runs_recent_main"));
        var recentIndexDdl = Assert.Single(generatedDdl, sql =>
            sql.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("idx_surf_runs_recent_main", StringComparison.OrdinalIgnoreCase));
        Assert.Matches("(?is).*Date.*DESC.*Id.*DESC.*", recentIndexDdl);

        await RunBackendCliAsync(databaseType, "migrate", connectionString);

        var finalRunColumns = DescribeColumns(db, "surf_runs");
        var finalRunIndexes = DescribeIndexes(db, "surf_runs");
        output.WriteLine($"surf_runs columns before conversion: {string.Join(", ", originalRunColumns)}");
        output.WriteLine($"surf_runs columns after migration: {string.Join(", ", finalRunColumns)}");
        output.WriteLine($"surf_runs indexes before conversion: {string.Join(", ", originalRunIndexes)}");
        output.WriteLine($"surf_runs indexes after migration: {string.Join(", ", finalRunIndexes)}");
        Assert.Equal(originalRunColumns.Select(column => column.Name).OrderBy(name => name),
                     finalRunColumns.Select(column => column.Name).OrderBy(name => name));
        Assert.Equal(originalRunIndexes.OrderBy(name => name), finalRunIndexes.OrderBy(name => name));
        Assert.Equal("Date", Assert.Single(finalRunColumns, column =>
            !string.Equals(column.Type, originalRunColumns.Single(before => before.Name == column.Name).Type,
                           StringComparison.OrdinalIgnoreCase)).Name,
            ignoreCase: true);

        Assert.Equal(originalMapCount, await db.Queryable<MapEntity>().CountAsync());
        Assert.Equal(originalRunCount, await db.Queryable<RunEntity>().CountAsync());
        var migratedMap = await db.Queryable<MapEntity>().SingleAsync();
        Assert.Equal(0, migratedMap.Bonuses);
        Assert.Equal(0, migratedMap.PlayCount);
        Assert.Equal(0f, migratedMap.TotalPlayTime);
        Assert.False(GetColumn(db, "surf_maps", nameof(MapEntity.Bonuses)).IsNullable);
        Assert.False(GetColumn(db, "surf_maps", nameof(MapEntity.PlayCount)).IsNullable);
        Assert.False(GetColumn(db, "surf_maps", nameof(MapEntity.TotalPlayTime)).IsNullable);
        Assert.True(IsBigInt(GetColumn(db, "surf_players", nameof(PlayerEntity.Points)).DataType));
        Assert.False(GetColumn(db, "surf_players", nameof(PlayerEntity.Points)).IsNullable);
        Assert.True(IsBigInt(GetColumn(db, "surf_player_track_scores", nameof(PlayerTrackScoreEntity.Points)).DataType));
        Assert.False(GetColumn(db, "surf_player_track_scores", nameof(PlayerTrackScoreEntity.Points)).IsNullable);
        Assert.True(db.DbMaintenance.IsAnyTable("surf_run_submissions", false));
        Assert.True(db.DbMaintenance.IsAnyTable("surf_score_recalc_outbox", false));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_surf_run_submissions_submission_unique"));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_unique"));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_pending"));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_player_track_scores_map_style_track"));
        var finalScoreIndexes = DescribeIndexes(db, "surf_player_track_scores");
        Assert.All(originalScoreIndexes, index =>
            Assert.Contains(index, finalScoreIndexes, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(seeded.PlayerPoints, await db.Queryable<PlayerEntity>()
                                                 .Where(row => row.SteamId == seeded.SteamId)
                                                 .Select(row => row.Points)
                                                 .SingleAsync());
        Assert.Equal(seeded.TrackScorePoints, await db.Queryable<PlayerTrackScoreEntity>()
                                                     .Where(row => row.MapId == seeded.MapId && row.SteamId == seeded.SteamId)
                                                     .Select(row => row.Points)
                                                     .SingleAsync());
        Assert.NotEmpty(await db.Queryable<PlayerBestRunEntity>().ToListAsync());

        var migratedPlayer = await db.Queryable<PlayerEntity>()
                                     .Where(row => row.SteamId == seeded.SteamId).SingleAsync();
        Assert.Equal(new DateTime(2024, 1, 1), migratedPlayer.JoinedAtUtc);
        Assert.Equal(new DateTime(2024, 1, 1), migratedPlayer.UpdatedAt);

        await AssertAtomicMapStatsAsync(databaseType, connectionString);

        // The two commands are designed to be safe to repeat after a process interruption.
        await RunBackendCliAsync(databaseType, "convert-run-dates", "--backup-confirmed", connectionString);
        await RunBackendCliAsync(databaseType, "migrate", connectionString);

        Assert.Equal(seeded.ExpectedDates,
                     (await db.Queryable<RunEntity>()
                              .Where(row => row.MapId == seeded.MapId)
                              .OrderBy(row => row.Style)
                              .Select(row => row.DateUnixTimeMilliseconds)
                              .ToListAsync())
                     .ToArray());
        Assert.Equal(originalMapCount, await db.Queryable<MapEntity>().CountAsync());
        Assert.Equal(originalRunCount, await db.Queryable<RunEntity>().CountAsync());
        Assert.Equal(seeded.PlayerPoints, await db.Queryable<PlayerEntity>()
                                                 .Where(row => row.SteamId == seeded.SteamId)
                                                 .Select(row => row.Points)
                                                 .SingleAsync());
        Assert.Equal(seeded.TrackScorePoints, await db.Queryable<PlayerTrackScoreEntity>()
                                                     .Where(row => row.MapId == seeded.MapId && row.SteamId == seeded.SteamId)
                                                     .Select(row => row.Points)
                                                     .SingleAsync());

        var repeatedPlayer = await db.Queryable<PlayerEntity>()
                                     .Where(row => row.SteamId == seeded.SteamId).SingleAsync();
        Assert.Equal(migratedPlayer.JoinedAtUtc, repeatedPlayer.JoinedAtUtc);

        // Simulate a process interruption after the safe first rename: master Date
        // became the durable backup, while the fully populated temporary BIGINT is
        // waiting to be promoted.  This exercises the LegacyRenamed branch rather
        // than only the happy path that starts from an untouched master schema.
        await RecreatePublishedMasterSchemaAsync(db, enableEnvironment);
        var interruptedSeeded = await SeedRepresentativeMasterRowsAsync(db);
        await ForceLegacyRenamedStateAsync(db);
        output.WriteLine("Resuming convert-run-dates from the LegacyRenamed durable state.");
        await RunBackendCliAsync(databaseType, "convert-run-dates", "--backup-confirmed", connectionString);
        await AssertConvertedDatesAsync(db, interruptedSeeded);
        await RunBackendCliAsync(databaseType, "migrate", connectionString);
        Assert.True(db.DbMaintenance.IsAnyTable("surf_run_submissions", false));
        Assert.True(db.DbMaintenance.IsAnyTable("surf_score_recalc_outbox", false));
    }

    private static SqlSugarScope CreateDatabase(DbType databaseType, string connectionString)
        => new(new ConnectionConfig
        {
            DbType = databaseType,
            ConnectionString = connectionString,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.IsDisabledDelete = true,
            },
        });

    private static async Task RecreatePublishedMasterSchemaAsync(SqlSugarScope db, string enableEnvironment)
    {
        // The opt-in guard is intentionally repeated immediately before destructive
        // DDL so copying this helper into a normal test cannot accidentally erase a DB.
        Assert.Equal("1", Environment.GetEnvironmentVariable(enableEnvironment));

        foreach (var table in PublishedAndBackendTablesToReset)
        {
            if (db.DbMaintenance.IsAnyTable(table, false))
            {
                Assert.True(db.DbMaintenance.DropTable(table));
            }
        }

        db.CodeFirst.InitTables(typeof(MasterMapRow),
                                typeof(MasterMapTrackRow),
                                typeof(MasterPlayerRow),
                                typeof(MasterPlayerMapStatsRow),
                                typeof(MasterPlayerBestRunRow),
                                typeof(MasterPlayerTrackScoreRow),
                                typeof(MasterRunRow),
                                typeof(MasterRunSegmentRow),
                                typeof(MasterReplayRow),
                                typeof(MasterZoneRow));

        await Task.CompletedTask;
    }

    private static async Task<(ulong MapId, long SteamId, uint PlayerPoints, uint TrackScorePoints, long[] ExpectedDates)>
        SeedRepresentativeMasterRowsAsync(SqlSugarScope db)
    {
        var map = new MasterMapRow { File = MasterMapName, Tier = 3, Stages = 2, BasePot = 750 };
        map.MapId = unchecked((ulong)await db.Insertable(map).ExecuteReturnBigIdentityAsync());

        const long steamId = 76561198000123456;
        const uint playerPoints = 123_456_789;
        const uint trackScorePoints = 987_654_321;
        await db.Insertable(new MasterPlayerRow
        {
            SteamId = steamId, Name = "Master migration acceptance", Points = playerPoints,
            UpdatedAt = new DateTime(2024, 1, 1),
        }).ExecuteCommandAsync();
        await db.Insertable(new MasterMapTrackRow { MapId = map.MapId, Track = 1, Tier = 4 }).ExecuteCommandAsync();
        await db.Insertable(new MasterPlayerMapStatsRow { SteamId = steamId, MapId = map.MapId, PlayTime = 50, PlayCount = 2 }).ExecuteCommandAsync();

        // Master used DateTime values; the database provider materializes them as
        // Unspecified.  The migration deliberately treats that existing convention as UTC.
        var dates = new[]
        {
            new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc),
            new DateTime(2024, 11, 12, 13, 14, 15, DateTimeKind.Utc),
        };
        var expectedDates = dates.Select(StorageServiceImpl.ToUnixTimeMilliseconds).ToArray();
        var runs = dates.Select((date, index) => new MasterRunRow
        {
            SteamId = steamId,
            MapId = map.MapId,
            RunType = RunType.Main,
            Style = index,
            Time = 55 + index,
            Jumps = (uint)(10 + index),
            Strafes = (uint)(20 + index),
            Sync = 95 + index,
            Date = date,
        }).ToArray();
        await db.Insertable(runs).ExecuteCommandAsync();

        var firstRun = await db.Queryable<MasterRunRow>()
                               .Where(row => row.Style == 0)
                               .SingleAsync();
        await db.Insertable(new MasterPlayerBestRunRow
        {
            SteamId = steamId, MapId = map.MapId, RunType = RunType.Main, Style = 0,
            RunId = firstRun.Id, BestTime = firstRun.Time, UpdatedAt = dates[0],
        }).ExecuteCommandAsync();
        await db.Insertable(new MasterPlayerTrackScoreRow
        {
            SteamId = steamId, MapId = map.MapId, Style = 0, Points = trackScorePoints, UpdatedAt = dates[0],
        }).ExecuteCommandAsync();
        await db.Insertable(new MasterRunSegmentRow
        {
            RunId = firstRun.Id, Stage = 1, Time = 20, Jumps = 3, Strafes = 4, Sync = 90, Date = dates[0],
        }).ExecuteCommandAsync();
        await db.Insertable(new MasterReplayRow
        {
            SteamId = steamId, MapId = map.MapId, RunId = firstRun.Id, Replay = "acceptance.replay",
            CreatedAt = dates[0], UpdatedAt = dates[0],
        }).ExecuteCommandAsync();
        await db.Insertable(new MasterZoneRow
        {
            MapId = map.MapId, Type = 0, Track = 0, Sequence = 0,
            Mins = "[0,0,0]", Maxs = "[1,1,1]", Center = "[0.5,0.5,0.5]", Angles = "[0,0,0]",
        }).ExecuteCommandAsync();

        return (map.MapId, steamId, playerPoints, trackScorePoints, expectedDates);
    }

    private async Task AssertAtomicMapStatsAsync(DbType databaseType, string connectionString)
    {
        var first = new StorageServiceImpl(databaseType, connectionString,
                                           NullLogger<StorageServiceImpl>.Instance,
                                           enableScoreRecalcWorker: false);
        var second = new StorageServiceImpl(databaseType, connectionString,
                                            NullLogger<StorageServiceImpl>.Instance,
                                            enableScoreRecalcWorker: false);
        try
        {
            // The schema is already supplied by the actual CLI under test; ordinary
            // backend replicas use this exact no-DDL startup path.
            first.Init(initializeSchema: false);
            second.Init(initializeSchema: false);
            var staleProfile = await first.GetMapInfo(MasterMapName);

            await Task.WhenAll(first.IncrementMapStatsAsync(MasterMapName, 10),
                               second.IncrementMapStatsAsync(MasterMapName, 20));
            await first.UpdateMapInfo(staleProfile);

            var verified = await second.GetMapInfo(MasterMapName);
            Assert.Equal(2, verified.PlayCount);
            Assert.Equal(30f, verified.TotalPlayTime);
            output.WriteLine($"{databaseType}: two independent atomic map-stat increments survived a stale map metadata write.");
        }
        finally
        {
            first.Shutdown();
            second.Shutdown();
        }
    }

    private static async Task ForceLegacyRenamedStateAsync(SqlSugarScope db)
    {
        const string temporaryColumn = "date_unix_milliseconds_migration";
        const string backupColumn = "date_datetime_migration_backup";
        Assert.True(db.DbMaintenance.AddColumn("surf_runs", new DbColumnInfo
        {
            DbColumnName = temporaryColumn,
            DataType = "bigint",
            IsNullable = true,
        }));

        var rows = await db.Queryable<LegacyRunDateMigrationRow>()
                           .OrderBy(row => row.Id)
                           .ToListAsync();
        Assert.NotEmpty(rows);
        foreach (var row in rows)
        {
            row.UnixMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(row.LegacyDate);
        }

        Assert.Equal(rows.Count, await db.Updateable(rows)
                                         .UpdateColumns(row => new { row.UnixMilliseconds })
                                         .ExecuteCommandAsync());
        Assert.True(db.DbMaintenance.RenameColumn("surf_runs", "Date", backupColumn));
        Assert.False(db.DbMaintenance.IsAnyColumn("surf_runs", "Date", false));
        Assert.True(db.DbMaintenance.IsAnyColumn("surf_runs", temporaryColumn, false));
        Assert.True(db.DbMaintenance.IsAnyColumn("surf_runs", backupColumn, false));
    }

    private static async Task AssertConvertedDatesAsync(
        SqlSugarScope db,
        (ulong MapId, long SteamId, uint PlayerPoints, uint TrackScorePoints, long[] ExpectedDates) seeded)
    {
        var converted = await db.Queryable<RunEntity>()
                                .Where(row => row.MapId == seeded.MapId)
                                .OrderBy(row => row.Style)
                                .Select(row => row.DateUnixTimeMilliseconds)
                                .ToListAsync();
        Assert.Equal(seeded.ExpectedDates, converted);
        Assert.True(IsBigInt(GetColumn(db, "surf_runs", "Date").DataType));
        Assert.False(db.DbMaintenance.IsAnyColumn("surf_runs", "date_unix_milliseconds_migration", false));
        Assert.False(db.DbMaintenance.IsAnyColumn("surf_runs", "date_datetime_migration_backup", false));
        Assert.True(db.DbMaintenance.IsAnyIndex("idx_surf_runs_recent_main"));
    }

    private static async Task RunBackendCliAsync(DbType databaseType, string operation, string connectionString)
        => await RunBackendCliAsync(databaseType, [operation], connectionString);

    private static async Task RunBackendCliAsync(DbType databaseType, string operation, string argument, string connectionString)
        => await RunBackendCliAsync(databaseType, [operation, argument], connectionString);

    private static async Task RunBackendCliAsync(DbType databaseType, string[] arguments, string connectionString)
    {
        var repositoryRoot = FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "run", "--no-build", "--no-restore", "--configuration", "Release",
                     "--project", Path.Combine(repositoryRoot, "Timer.Backend", "Timer.Backend.csproj"), "--",
                 }.Concat(arguments))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["TimerBackend__Database__Type"] = databaseType == DbType.MySql ? "mysql" : "postgresql";
        startInfo.Environment["TimerBackend__Database__ConnectionString"] = connectionString;
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("Could not start the Timer.Backend CLI.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Timer.Backend {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{output}{Environment.NewLine}stderr:{Environment.NewLine}{error}");
        }
    }

    private static DbColumnInfo GetColumn(SqlSugarScope db, string table, string column)
        => db.DbMaintenance.GetColumnInfosByTableName(table, false)
             .Single(info => string.Equals(info.DbColumnName, column, StringComparison.OrdinalIgnoreCase));

    private static (string Name, string Type, bool IsNullable)[] DescribeColumns(SqlSugarScope db, string table)
        => db.DbMaintenance.GetColumnInfosByTableName(table, false)
             .Select(info => (info.DbColumnName, info.DataType, info.IsNullable))
             .OrderBy(info => info.DbColumnName, StringComparer.OrdinalIgnoreCase)
             .ToArray();

    private static string[] DescribeIndexes(SqlSugarScope db, string table)
        => db.DbMaintenance.GetIndexList(table)
             .Distinct(StringComparer.OrdinalIgnoreCase)
             .OrderBy(index => index, StringComparer.OrdinalIgnoreCase)
             .ToArray();

    private static bool IsBigInt(string? type)
    {
        var normalized = (type ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bigint" or "int8" or "long" or "int64";
    }

    private static bool IsTemporal(string? type)
    {
        var normalized = (type ?? string.Empty).Trim();
        return normalized.StartsWith("date", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("datetime", StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith("timestamp", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDisposableLoopbackDatabase(string connectionString, DbType databaseType, string enableEnvironment)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var server = ReadConnectionValue(builder, "Server", "Host");
        var database = ReadConnectionValue(builder, "Database", "Initial Catalog");
        var defaultPort = databaseType == DbType.MySql ? 3306 : 5432;
        var port = int.TryParse(ReadConnectionValue(builder, "Port"), out var parsedPort) ? parsedPort : defaultPort;
        var isLoopback = string.Equals(server, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(server, "::1", StringComparison.OrdinalIgnoreCase);
        if (!isLoopback || port < 10240 || port > 65535
            || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{enableEnvironment}=1 is accepted only for a loopback disposable {databaseType} test database on a high port with 'test' in its database name.");
        }
    }

    private static string ReadConnectionValue(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Timer.Backend", "Timer.Backend.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Timer repository root for the backend CLI acceptance test.");
    }

    private static readonly string[] PublishedAndBackendTablesToReset =
    [
        "surf_score_recalc_outbox", "surf_run_submissions", "surf_zones", "surf_runs_replay",
        "surf_runs_segments", "surf_runs", "surf_player_track_scores", "surf_player_best_runs",
        "surf_player_map_stats", "surf_players", "surf_maps_tracks", "surf_maps",
    ];

    private sealed class MasterSqlMigrationFactAttribute : FactAttribute
    {
        public MasterSqlMigrationFactAttribute(string connectionEnvironment, string enableEnvironment)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(enableEnvironment), "1", StringComparison.Ordinal))
            {
                Skip = $"Set {enableEnvironment}=1 and {connectionEnvironment} to an explicitly disposable loopback test DB.";
            }
            else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionEnvironment)))
            {
                Skip = $"Set {connectionEnvironment} to an explicitly disposable loopback test DB.";
            }
        }
    }

    // Physical master-schema snapshot.  Keep this separate from current entities:
    // Map totals, the new Date BIGINT property, and the new score covering index
    // must be added by the commands under test rather than by test setup.
    [SugarTable("surf_maps")]
    [SugarIndex("idx_surf_maps_file", nameof(File), OrderByType.Asc, true)]
    private sealed class MasterMapRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong MapId { get; set; }
        public string File { get; set; } = "INVALID_FILE";
        public byte Tier { get; set; }
        public ushort Stages { get; set; }
        public int BasePot { get; set; }
    }

    [SugarTable("surf_maps_tracks")]
    private sealed class MasterMapTrackRow
    {
        [SugarColumn(IsPrimaryKey = true)] public ulong MapId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ushort Track { get; set; }
        public byte Tier { get; set; }
    }

    [SugarTable("surf_players")]
    [SugarIndex("idx_surf_players_steamid", nameof(SteamId), OrderByType.Asc, true)]
    private sealed class MasterPlayerRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnDataType = "bigint")] public long SteamId { get; set; }
        [SugarColumn(Length = 192)] public string Name { get; set; } = string.Empty;
        public uint Points { get; set; }
        public uint Runs { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [SugarTable("surf_player_map_stats")]
    [SugarIndex("idx_player_map_stats_unique", nameof(SteamId), OrderByType.Asc, nameof(MapId), OrderByType.Asc, true)]
    private sealed class MasterPlayerMapStatsRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnDataType = "bigint")] public long SteamId { get; set; }
        public ulong MapId { get; set; }
        public float PlayTime { get; set; }
        public int PlayCount { get; set; }
    }

    [SugarTable("surf_player_best_runs")]
    [SugarIndex("idx_player_best_runs_unique", nameof(SteamId), OrderByType.Asc, nameof(MapId), OrderByType.Asc,
                nameof(RunType), OrderByType.Asc, nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc,
                nameof(Stage), OrderByType.Asc, true)]
    [SugarIndex("idx_player_best_runs_rank", nameof(MapId), OrderByType.Asc, nameof(RunType), OrderByType.Asc,
                nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc, nameof(Stage), OrderByType.Asc,
                nameof(BestTime), OrderByType.Asc, nameof(SteamId), OrderByType.Asc)]
    private sealed class MasterPlayerBestRunRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnDataType = "bigint")] public long SteamId { get; set; }
        public ulong MapId { get; set; }
        public RunType RunType { get; set; }
        public ushort Stage { get; set; }
        public int Style { get; set; }
        public ushort Track { get; set; }
        public ulong RunId { get; set; }
        public float BestTime { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [SugarTable("surf_player_track_scores")]
    [SugarIndex("idx_player_track_scores_steam_map_style_track", nameof(SteamId), OrderByType.Asc,
                nameof(MapId), OrderByType.Asc, nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc, true)]
    private sealed class MasterPlayerTrackScoreRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnDataType = "bigint")] public long SteamId { get; set; }
        public ulong MapId { get; set; }
        public int Style { get; set; }
        public ushort Track { get; set; }
        public uint Points { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [SugarTable("surf_runs")]
    [SugarIndex("idx_surf_runs_map_style_track_time", nameof(MapId), OrderByType.Asc, nameof(RunType), OrderByType.Asc,
                nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc, nameof(Stage), OrderByType.Asc, nameof(Time), OrderByType.Asc)]
    [SugarIndex("idx_surf_runs_steam_map_style_track_time", nameof(SteamId), OrderByType.Asc, nameof(MapId), OrderByType.Asc,
                nameof(RunType), OrderByType.Asc, nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc,
                nameof(Stage), OrderByType.Asc, nameof(Time), OrderByType.Asc)]
    [SugarIndex("idx_surf_runs_recent_main", nameof(MapId), OrderByType.Asc, nameof(SteamId), OrderByType.Asc,
                nameof(RunType), OrderByType.Asc, nameof(Stage), OrderByType.Asc, nameof(Date), OrderByType.Desc,
                nameof(Id), OrderByType.Desc)]
    [SugarIndex("idx_surf_runs_rank_cover", nameof(MapId), OrderByType.Asc, nameof(RunType), OrderByType.Asc,
                nameof(Style), OrderByType.Asc, nameof(Track), OrderByType.Asc, nameof(Stage), OrderByType.Asc,
                nameof(Time), OrderByType.Asc, nameof(SteamId), OrderByType.Asc)]
    private sealed class MasterRunRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnDataType = "bigint")] public long SteamId { get; set; }
        public ulong MapId { get; set; }
        public RunType RunType { get; set; }
        public ushort Stage { get; set; }
        public int Style { get; set; }
        public ushort Track { get; set; }
        public float Time { get; set; }
        public uint Jumps { get; set; }
        public uint Strafes { get; set; }
        public float Sync { get; set; }
        public float VelocityStartX { get; set; }
        public float VelocityStartY { get; set; }
        public float VelocityStartZ { get; set; }
        public float VelocityEndX { get; set; }
        public float VelocityEndY { get; set; }
        public float VelocityEndZ { get; set; }
        public float VelocityMaxX { get; set; }
        public float VelocityMaxY { get; set; }
        public float VelocityMaxZ { get; set; }
        public float VelocityAvgX { get; set; }
        public float VelocityAvgY { get; set; }
        public float VelocityAvgZ { get; set; }
        public DateTime Date { get; set; }
    }

    [SugarTable("surf_runs")]
    private sealed class LegacyRunDateMigrationRow
    {
        [SugarColumn(IsPrimaryKey = true)] public ulong Id { get; set; }
        [SugarColumn(ColumnName = "Date")] public DateTime LegacyDate { get; set; }
        [SugarColumn(ColumnName = "date_unix_milliseconds_migration", ColumnDataType = "bigint")]
        public long? UnixMilliseconds { get; set; }
    }

    [SugarTable("surf_runs_segments")]
    [SugarIndex("idx_surf_runs_segments_runid_stage", nameof(RunId), OrderByType.Asc, nameof(Stage), OrderByType.Asc)]
    private sealed class MasterRunSegmentRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        public ulong RunId { get; set; }
        public ushort Stage { get; set; }
        public float Time { get; set; }
        public uint Jumps { get; set; }
        public uint Strafes { get; set; }
        public float Sync { get; set; }
        public float VelocityStartX { get; set; }
        public float VelocityStartY { get; set; }
        public float VelocityStartZ { get; set; }
        public float VelocityEndX { get; set; }
        public float VelocityEndY { get; set; }
        public float VelocityEndZ { get; set; }
        public float VelocityMaxX { get; set; }
        public float VelocityMaxY { get; set; }
        public float VelocityMaxZ { get; set; }
        public float VelocityAvgX { get; set; }
        public float VelocityAvgY { get; set; }
        public float VelocityAvgZ { get; set; }
        public DateTime Date { get; set; }
    }

    [SugarTable("surf_runs_replay")]
    [SugarIndex("idx_surf_runs_replay_map", nameof(MapId), OrderByType.Asc)]
    [SugarIndex("idx_surf_runs_replay_runid", nameof(RunId), OrderByType.Asc)]
    private sealed class MasterReplayRow
    {
        [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")] public long SteamId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong MapId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong RunId { get; set; }
        public string Replay { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [SugarTable("surf_zones")]
    [SugarIndex("idx_surf_zones_map_track_type_seq", nameof(MapId), OrderByType.Asc, nameof(Track), OrderByType.Asc,
                nameof(Type), OrderByType.Asc, nameof(Sequence), OrderByType.Asc)]
    private sealed class MasterZoneRow
    {
        [SugarColumn(IsPrimaryKey = true, IsIdentity = true)] public ulong Id { get; set; }
        public ulong MapId { get; set; }
        public int Type { get; set; }
        public ushort Track { get; set; }
        public ushort Sequence { get; set; }
        [SugarColumn(ColumnDataType = "text")] public string Mins { get; set; } = string.Empty;
        [SugarColumn(ColumnDataType = "text")] public string Maxs { get; set; } = string.Empty;
        [SugarColumn(ColumnDataType = "text")] public string Center { get; set; } = string.Empty;
        [SugarColumn(ColumnDataType = "text")] public string Angles { get; set; } = string.Empty;
        [SugarColumn(IsNullable = true, ColumnDataType = "text")] public string? TeleportOrigin { get; set; }
        [SugarColumn(IsNullable = true, ColumnDataType = "text")] public string? TeleportAngles { get; set; }
        [SugarColumn(IsNullable = true, ColumnDataType = "text")] public string? Config { get; set; }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlSchemaMutationCollection
{
    public const string Name = "Disposable SQL schema mutation";
}
