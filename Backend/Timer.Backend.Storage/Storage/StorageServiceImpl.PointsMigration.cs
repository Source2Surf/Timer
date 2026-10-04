using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private const string MasterPlayersTableName = "surf_players";
    private const string MasterPlayerTrackScoresTableName = "surf_player_track_scores";
    private const string MasterPointsColumnName = "Points";
    private const string MasterPlayersSteamIdIndexName = "idx_surf_players_steamid";
    private const string MasterPlayerTrackScoresUniqueIndexName = "idx_player_track_scores_steam_map_style_track";
    private const int MasterPointsMigrationBatchSize = 1_000;

    // These are the two published-master tables whose uint CLR fields used to
    // materialize as signed SQL INT. Keep this intentionally narrow: the one-shot
    // migration must not use the current CodeFirst model as a general schema
    // reconciler for a database that did not originate from master.
    private static readonly string[] MasterPlayersSchemaColumns =
    [
        "Id", "SteamId", "Name", MasterPointsColumnName, "Runs", "UpdatedAt",
    ];

    private static readonly string[] MasterPlayerTrackScoresSchemaColumns =
    [
        "Id", "SteamId", "MapId", "Style", "Track", MasterPointsColumnName, "UpdatedAt",
    ];

    /// <summary>
    /// Widens the two published-master score columns from signed SQL INT to
    /// signed BIGINT. The CLR contract remains <see cref="uint"/>, so every
    /// value is checked before and after the DDL rather than being silently
    /// reinterpreted by the provider.
    /// </summary>
    internal async Task MigrateMasterPointsColumnsAsync()
    {
        EnsurePointsMigrationProvider();

        await MigrateMasterPointsColumnAsync(MasterPlayersTableName,
                                              MasterPlayersSchemaColumns,
                                              MasterPlayersSteamIdIndexName,
                                              ReadMasterPlayerPointsBatchAsync,
                                              row => row.Id,
                                              row => row.Points,
                                              preserveIndexes: false);
        await MigrateMasterPointsColumnAsync(MasterPlayerTrackScoresTableName,
                                              MasterPlayerTrackScoresSchemaColumns,
                                              MasterPlayerTrackScoresUniqueIndexName,
                                              ReadMasterPlayerTrackScorePointsBatchAsync,
                                              row => row.Id,
                                              row => row.Points,
                                              preserveIndexes: true);
    }

    /// <summary>
    /// Prevents normal CodeFirst startup from changing a legacy master INT
    /// column implicitly. The dedicated master migration deliberately calls
    /// <see cref="MigrateMasterPointsColumnsAsync"/> before this guard.
    /// </summary>
    /// <param name="allowBootstrapRecovery">
    /// True only immediately before CodeFirst runs. Lets startup complete a first-time schema
    /// initialization that was interrupted part-way (a points table or its unique index not
    /// yet created); without it such a database could never start or be migrated again.
    /// </param>
    internal void EnsurePointsColumnsReadyForRuntime(bool allowBootstrapRecovery = false)
    {
        // SQLite is only used by unit tests. The production schema migration is
        // deliberately constrained to MySQL/MariaDB and PostgreSQL.
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            return;
        }

        var playersExists = _db.DbMaintenance.IsAnyTable(MasterPlayersTableName, false);
        var trackScoresExists = _db.DbMaintenance.IsAnyTable(MasterPlayerTrackScoresTableName, false);
        if (!playersExists && !trackScoresExists)
        {
            // A genuine fresh installation still needs CodeFirst to create both
            // tables. Any partial database is unsafe to treat as fresh.
            return;
        }

        if (allowBootstrapRecovery && IsInterruptedPointsBootstrap(playersExists, trackScoresExists))
        {
            _logger.LogWarning(
                "The points tables look like an interrupted first-time schema initialization: every existing points table already uses the current BIGINT column. Completing the initialization.");
            return;
        }

        if (!playersExists || !trackScoresExists)
        {
            throw new InvalidOperationException(
                "The published master points tables are incomplete. Run the one-shot migrate command against a verified master database before normal startup.");
        }

        EnsureMasterPointsColumnReadyForRuntime(MasterPlayersTableName,
                                                 MasterPlayersSchemaColumns,
                                                 MasterPlayersSteamIdIndexName);
        EnsureMasterPointsColumnReadyForRuntime(MasterPlayerTrackScoresTableName,
                                                 MasterPlayerTrackScoresSchemaColumns,
                                                 MasterPlayerTrackScoresUniqueIndexName);
    }

    /// <summary>
    /// A published master table has a signed INT Points column; tables this code creates use
    /// BIGINT from the start. So a partial state (a missing table or unique index) in which every
    /// existing points table is already BIGINT was left by an interrupted CodeFirst run, not by a
    /// legacy master database that must go through the one-shot migrate command.
    /// </summary>
    private bool IsInterruptedPointsBootstrap(bool playersExists, bool trackScoresExists)
    {
        var complete = playersExists
                       && trackScoresExists
                       && ReadTableIndexes(MasterPlayersTableName).Contains(MasterPlayersSteamIdIndexName)
                       && ReadTableIndexes(MasterPlayerTrackScoresTableName).Contains(MasterPlayerTrackScoresUniqueIndexName);
        if (complete)
        {
            return false;
        }

        return (!playersExists || HasCurrentPointsColumn(MasterPlayersTableName))
               && (!trackScoresExists || HasCurrentPointsColumn(MasterPlayerTrackScoresTableName));
    }

    private bool HasCurrentPointsColumn(string tableName)
    {
        var column = FindColumn(_db.DbMaintenance.GetColumnInfosByTableName(tableName, false), MasterPointsColumnName);
        return column is not null && IsRequiredSignedBigIntColumn(column) && !column.IsNullable;
    }

    /// <summary>
    /// CodeFirst creates declared indexes only together with their table, so an initialization
    /// interrupted between the two leaves the unique index missing forever. Create it here.
    /// A unique index cannot be created over duplicate rows, so lost uniqueness still fails loudly.
    /// </summary>
    private void EnsurePointsTableUniqueIndexes()
    {
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            return;
        }

        if (_db.DbMaintenance.IsAnyTable(MasterPlayersTableName, false)
            && !ReadTableIndexes(MasterPlayersTableName).Contains(MasterPlayersSteamIdIndexName))
        {
            _db.DbMaintenance.CreateIndex(MasterPlayersTableName,
                                           [nameof(PlayerEntity.SteamId)],
                                           MasterPlayersSteamIdIndexName,
                                           true);
        }

        if (_db.DbMaintenance.IsAnyTable(MasterPlayerTrackScoresTableName, false)
            && !ReadTableIndexes(MasterPlayerTrackScoresTableName).Contains(MasterPlayerTrackScoresUniqueIndexName))
        {
            _db.DbMaintenance.CreateIndex(MasterPlayerTrackScoresTableName,
                                           [
                                               nameof(PlayerTrackScoreEntity.SteamId),
                                               nameof(PlayerTrackScoreEntity.MapId),
                                               nameof(PlayerTrackScoreEntity.Style),
                                               nameof(PlayerTrackScoreEntity.Track),
                                           ],
                                           MasterPlayerTrackScoresUniqueIndexName,
                                           true);
        }
    }

    private void EnsurePointsMigrationProvider()
    {
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            throw new NotSupportedException(
                "Master Points widening supports only MySQL/MariaDB and PostgreSQL; LiteDB and test-only SQLite are not converted.");
        }
    }

    private async Task MigrateMasterPointsColumnAsync<T>(string                          tableName,
                                                           IReadOnlyList<string>           expectedSchemaColumns,
                                                           string                          requiredPublishedIndexName,
                                                           Func<ulong?, Task<List<T>>>     readBatch,
                                                           Func<T, ulong>                   getId,
                                                           Func<T, long?>                   getPoints,
                                                           bool                             preserveIndexes)
        where T : class, new()
    {
        if (!_db.DbMaintenance.IsAnyTable(tableName, false))
        {
            throw new InvalidOperationException(
                $"The published master table '{tableName}' was not found. This migration never creates or guesses a schema.");
        }

        var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        ValidatePublishedMasterPointsSchema(tableName, columns, expectedSchemaColumns);
        EnsureRequiredMasterPointsIndex(tableName, requiredPublishedIndexName);

        var pointsColumn = FindColumn(columns, MasterPointsColumnName)
                           ?? throw new InvalidOperationException(
                               $"The published master table '{tableName}' has no {MasterPointsColumnName} column.");
        if (pointsColumn.IsNullable)
        {
            throw new InvalidOperationException(
                $"{tableName}.{MasterPointsColumnName} is nullable. Refusing to turn an unexpected nullable score schema into the required non-null BIGINT.");
        }

        var alreadyBigInt = IsRequiredSignedBigIntColumn(pointsColumn);
        if (!alreadyBigInt && !IsPublishedMasterSignedIntColumn(pointsColumn))
        {
            throw new InvalidOperationException(
                $"{tableName}.{MasterPointsColumnName} has unexpected type '{pointsColumn.DataType}'. " +
                "Only the published master signed INT or an already migrated signed BIGINT is accepted.");
        }

        // Take a typed, ordered snapshot before altering metadata. It includes a
        // row count, min/max and a per-(Id, Points) checksum, so both data loss
        // and a changed value are caught without retaining all historical rows
        // in memory. Writers must remain stopped throughout the one-shot command.
        var before = await CapturePointsColumnSnapshotAsync(tableName, readBatch, getId, getPoints);

        if (alreadyBigInt)
        {
            _logger.LogInformation(
                "{Table}.{Column} is already a verified non-null BIGINT ({Rows} rows, min={MinPoints}, max={MaxPoints}).",
                tableName, MasterPointsColumnName, before.RowCount, before.MinimumPoints, before.MaximumPoints);
            return;
        }

        HashSet<string>? indexesBefore = preserveIndexes ? ReadTableIndexes(tableName) : null;

        pointsColumn.DataType = "bigint";
        pointsColumn.Length = 0;
        pointsColumn.IsNullable = false;
        if (!_db.DbMaintenance.UpdateColumn(tableName, pointsColumn))
        {
            throw new InvalidOperationException(
                $"Could not widen {tableName}.{MasterPointsColumnName} from signed INT to non-null BIGINT.");
        }

        var columnsAfter = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        ValidatePublishedMasterPointsSchema(tableName, columnsAfter, expectedSchemaColumns);
        EnsureRequiredMasterPointsIndex(tableName, requiredPublishedIndexName);
        var pointsColumnAfter = FindColumn(columnsAfter, MasterPointsColumnName)
                                ?? throw new InvalidOperationException(
                                    $"{tableName}.{MasterPointsColumnName} disappeared while widening it to BIGINT.");
        if (!IsRequiredSignedBigIntColumn(pointsColumnAfter) || pointsColumnAfter.IsNullable)
        {
            throw new InvalidOperationException(
                $"{tableName}.{MasterPointsColumnName} was not widened to a non-null signed BIGINT (actual '{pointsColumnAfter.DataType}').");
        }

        if (indexesBefore is not null)
        {
            var indexesAfter = ReadTableIndexes(tableName);
            var missingIndexes = indexesBefore.Where(index => !indexesAfter.Contains(index))
                                             .OrderBy(index => index, StringComparer.Ordinal)
                                             .ToArray();
            if (missingIndexes.Length != 0)
            {
                throw new InvalidOperationException(
                    $"Widening {tableName}.{MasterPointsColumnName} removed existing indexes ({string.Join(", ", missingIndexes)}). " +
                    "Do not serve this database until the verified backup or index definitions have been restored.");
            }
        }

        var after = await CapturePointsColumnSnapshotAsync(tableName, readBatch, getId, getPoints);
        if (before != after)
        {
            throw new InvalidOperationException(
                $"{tableName}.{MasterPointsColumnName} values changed while widening it to BIGINT. " +
                "Keep writers stopped and restore or inspect the verified backup before retrying.");
        }

        _logger.LogInformation(
            "Widened {Table}.{Column} from signed INT to non-null BIGINT and verified {Rows} rows (min={MinPoints}, max={MaxPoints}).",
            tableName, MasterPointsColumnName, after.RowCount, after.MinimumPoints, after.MaximumPoints);
    }

    private void EnsureMasterPointsColumnReadyForRuntime(string                     tableName,
                                                          IReadOnlyList<string>      expectedSchemaColumns,
                                                          string                     requiredPublishedIndexName)
    {
        var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        ValidatePublishedMasterPointsSchema(tableName, columns, expectedSchemaColumns);
        EnsureRequiredMasterPointsIndex(tableName, requiredPublishedIndexName);

        var pointsColumn = FindColumn(columns, MasterPointsColumnName);
        if (pointsColumn is null || !IsRequiredSignedBigIntColumn(pointsColumn) || pointsColumn.IsNullable)
        {
            var actualType = pointsColumn?.DataType ?? "missing";
            throw new InvalidOperationException(
                $"{tableName}.{MasterPointsColumnName} is not the required non-null signed BIGINT (actual '{actualType}'). " +
                "Stop writers and run the one-shot migrate command before normal startup or CodeFirst initialization.");
        }
    }

    private static void ValidatePublishedMasterPointsSchema(string                     tableName,
                                                              IEnumerable<DbColumnInfo> columns,
                                                              IReadOnlyList<string>      expectedSchemaColumns)
    {
        var names = new HashSet<string>(columns.Select(column => column.DbColumnName), StringComparer.OrdinalIgnoreCase);
        foreach (var required in expectedSchemaColumns)
        {
            if (!names.Contains(required))
            {
                throw new InvalidOperationException(
                    $"{tableName} is not the published master schema: required column '{required}' is missing.");
            }
        }

        var expected = new HashSet<string>(expectedSchemaColumns, StringComparer.OrdinalIgnoreCase);
        // This is our additive player migration, not an unrelated custom schema.
        // Accept both the untouched master shape and a repeat migration after upgrade.
        if (tableName == MasterPlayersTableName) expected.Add(nameof(PlayerEntity.JoinedAtUtc));
        var unexpected = names.Where(name => !expected.Contains(name))
                              .OrderBy(name => name, StringComparer.Ordinal)
                              .ToArray();
        if (unexpected.Length != 0)
        {
            throw new InvalidOperationException(
                $"{tableName} is not exactly the published master schema (unexpected columns: {string.Join(", ", unexpected)}). " +
                "Refusing to reinterpret a different score schema.");
        }
    }

    private static bool IsPublishedMasterSignedIntColumn(DbColumnInfo column)
        => !IsUnsignedColumn(column)
           && NormalizeColumnType(column.DataType) is "int" or "integer" or "int4" or "int32";

    private static bool IsRequiredSignedBigIntColumn(DbColumnInfo column)
        => !IsUnsignedColumn(column) && IsBigIntColumn(column);

    private static bool IsUnsignedColumn(DbColumnInfo column)
        => column.IsUnsigned == true
           || column.DataType?.Contains("unsigned", StringComparison.OrdinalIgnoreCase) == true;

    private void EnsureRequiredMasterPointsIndex(string tableName, string requiredIndexName)
    {
        if (!ReadTableIndexes(tableName).Contains(requiredIndexName))
        {
            throw new InvalidOperationException(
                $"{tableName} is not the published master schema: required index '{requiredIndexName}' is missing. " +
                "Refusing to serve or migrate a score table whose uniqueness/index contract may have been lost.");
        }
    }

    private HashSet<string> ReadTableIndexes(string tableName)
        => _db.DbMaintenance.GetIndexList(tableName)
              .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private async Task<PointsColumnSnapshot> CapturePointsColumnSnapshotAsync<T>(string                      tableName,
                                                                                    Func<ulong?, Task<List<T>>> readBatch,
                                                                                    Func<T, ulong>               getId,
                                                                                    Func<T, long?>               getPoints)
        where T : class, new()
    {
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var encodedValue = new byte[sizeof(ulong) + sizeof(long)];
        var rowCount = 0L;
        long? minimumPoints = null;
        long? maximumPoints = null;
        ulong? lastId = null;

        while (true)
        {
            var batch = await readBatch(lastId);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var id = getId(row);
                if (lastId.HasValue && id <= lastId.Value)
                {
                    throw new InvalidOperationException(
                        $"{tableName}.{MasterPointsColumnName} snapshot was not ordered by a unique Id. Keep writers stopped and inspect the published master schema.");
                }

                var points = getPoints(row);
                if (points is not { } value)
                {
                    throw new InvalidOperationException(
                        $"{tableName}.Id={id} has a NULL {MasterPointsColumnName} value despite the master non-null invariant.");
                }

                if (value < 0 || value > uint.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"{tableName}.Id={id} has {MasterPointsColumnName}={value}, outside the C# uint score contract.");
                }

                BinaryPrimitives.WriteUInt64LittleEndian(encodedValue.AsSpan(0, sizeof(ulong)), id);
                BinaryPrimitives.WriteInt64LittleEndian(encodedValue.AsSpan(sizeof(ulong), sizeof(long)), value);
                checksum.AppendData(encodedValue);

                rowCount = checked(rowCount + 1);
                minimumPoints = minimumPoints is null ? value : Math.Min(minimumPoints.Value, value);
                maximumPoints = maximumPoints is null ? value : Math.Max(maximumPoints.Value, value);
                lastId = id;
            }
        }

        var countedRows = await _db.Queryable<T>().CountAsync();
        if (countedRows != rowCount)
        {
            throw new InvalidOperationException(
                $"{tableName} changed while {MasterPointsColumnName} values were being snapshotted ({rowCount} read, {countedRows} counted). " +
                "Stop writers and rerun the one-shot migrate command.");
        }

        return new PointsColumnSnapshot(rowCount,
                                        minimumPoints,
                                        maximumPoints,
                                        Convert.ToHexString(checksum.GetHashAndReset()));
    }

    private async Task<List<MasterPlayerPointsMigrationRow>> ReadMasterPlayerPointsBatchAsync(ulong? lastId)
    {
        var query = _db.Queryable<MasterPlayerPointsMigrationRow>().OrderBy(row => row.Id);
        if (lastId.HasValue)
        {
            query = query.Where(row => row.Id > lastId.Value);
        }

        return await query.Take(MasterPointsMigrationBatchSize).ToListAsync();
    }

    private async Task<List<MasterPlayerTrackScorePointsMigrationRow>> ReadMasterPlayerTrackScorePointsBatchAsync(ulong? lastId)
    {
        var query = _db.Queryable<MasterPlayerTrackScorePointsMigrationRow>().OrderBy(row => row.Id);
        if (lastId.HasValue)
        {
            query = query.Where(row => row.Id > lastId.Value);
        }

        return await query.Take(MasterPointsMigrationBatchSize).ToListAsync();
    }

    private readonly record struct PointsColumnSnapshot(long RowCount,
                                                          long? MinimumPoints,
                                                          long? MaximumPoints,
                                                          string ValueChecksum);

    [SugarTable(MasterPlayersTableName)]
    private sealed class MasterPlayerPointsMigrationRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = MasterPointsColumnName, ColumnDataType = "bigint")]
        public long? Points { get; set; }
    }

    [SugarTable(MasterPlayerTrackScoresTableName)]
    private sealed class MasterPlayerTrackScorePointsMigrationRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = MasterPointsColumnName, ColumnDataType = "bigint")]
        public long? Points { get; set; }
    }
}
