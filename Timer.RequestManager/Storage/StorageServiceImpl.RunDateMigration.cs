using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private const string MasterRunTableName = "surf_runs";
    private const string MasterRunDateColumnName = "Date";
    private const string RunDateUnixMillisecondsMigrationColumnName = "date_unix_milliseconds_migration";
    private const string RunDateDateTimeBackupColumnName = "date_datetime_migration_backup";
    private const string RunDateRecentIndexName = "idx_surf_runs_recent_main";
    private const int RunDateMigrationBatchSize = 1_000;

    // This is a snapshot of the published master RunEntity schema, before this
    // branch introduced the Unix-millisecond representation.  Do not derive this
    // from the current entity: the conversion must reject an unpublished/interim
    // surf_runs shape instead of attempting to reinterpret it.
    private static readonly string[] MasterRunSchemaColumns =
    [
        "Id", "SteamId", "MapId", "RunType", "Stage", "Style", "Track", "Time", "Jumps", "Strafes", "Sync",
        "VelocityStartX", "VelocityStartY", "VelocityStartZ",
        "VelocityEndX", "VelocityEndY", "VelocityEndZ",
        "VelocityMaxX", "VelocityMaxY", "VelocityMaxZ",
        "VelocityAvgX", "VelocityAvgY", "VelocityAvgZ",
        MasterRunDateColumnName,
    ];

    /// <summary>
    /// Converts the published master <c>surf_runs.Date</c> SQL date-time column to a
    /// UTC Unix-milliseconds BIGINT.  This is deliberately separate from the
    /// additive backend migration: an operator must stop writers and explicitly
    /// acknowledge a restorable backup before a temporal column is renamed/dropped.
    ///
    /// The temporary and backup columns are a durable state machine.  A failed
    /// MySQL DDL operation is therefore safe to resume by rerunning this method;
    /// PostgreSQL can also resume rather than relying on transactional DDL.
    /// </summary>
    internal async Task ConvertMasterRunDateColumnAsync(bool backupConfirmed)
    {
        if (!backupConfirmed)
        {
            throw new InvalidOperationException(
                "Refusing to convert surf_runs.Date without explicit backup confirmation. " +
                "Stop all writers, verify a restorable backup, then rerun convert-run-dates with --backup-confirmed.");
        }

        EnsureRunDateMigrationProvider();

        if (!_db.DbMaintenance.IsAnyTable(MasterRunTableName, false))
        {
            throw new InvalidOperationException(
                "The published master surf_runs table was not found. This conversion never creates or guesses a schema.");
        }

        var rowsBefore = await CountMasterRunRowsAsync();
        var layout = ReadRunDateMigrationLayout();

        switch (layout.State)
        {
            case RunDateMigrationState.Completed:
                await VerifyCurrentUnixRunDateRowsAsync(rowsBefore);
                EnsureRunDateRecentIndex(replaceExisting: false);
                _logger.LogInformation(
                    "surf_runs.Date is already a verified Unix-milliseconds BIGINT ({RunRows} rows); no date conversion was required.",
                    rowsBefore);
                return;

            case RunDateMigrationState.LegacyDateTime:
                AddRunDateMigrationColumn();
                layout = ReadRunDateMigrationLayout();
                goto case RunDateMigrationState.Backfilling;

            case RunDateMigrationState.Backfilling:
                await BackfillRunDateUnixMillisecondsAsync(rowsBefore);
                await VerifyDateAndTemporaryRunDateRowsAsync(rowsBefore);
                RenameLegacyDateToBackup(layout.DateColumn!);
                layout = ReadRunDateMigrationLayout();
                goto case RunDateMigrationState.LegacyRenamed;

            case RunDateMigrationState.LegacyRenamed:
                await VerifyBackupAndTemporaryRunDateRowsAsync(rowsBefore);
                RenameUnixMigrationColumnToDate(layout.UnixMigrationColumn!);
                layout = ReadRunDateMigrationLayout();
                goto case RunDateMigrationState.NewDateAwaitingCleanup;

            case RunDateMigrationState.NewDateAwaitingCleanup:
                await VerifyBackupAndPromotedRunDateRowsAsync(rowsBefore);
                EnsureNonNullableBigIntDateColumn(layout.DateColumn!);
                EnsureRunDateRecentIndex(replaceExisting: true);
                DropLegacyDateBackup(layout.LegacyBackupColumn!);
                layout = ReadRunDateMigrationLayout();
                goto case RunDateMigrationState.Completed;

            default:
                throw new InvalidOperationException($"Unsupported surf_runs.Date conversion state {layout.State}.");
        }
    }

    /// <summary>
    /// Guards normal CodeFirst startup and the separate backend migration.  Existing
    /// master databases must be converted through <see cref="ConvertMasterRunDateColumnAsync"/>
    /// first; otherwise CodeFirst might issue an unsafe provider-specific type change.
    /// Fresh installs have no surf_runs table yet and remain supported.
    /// </summary>
    internal void EnsureRunDateColumnReadyForRuntime()
    {
        if (!_db.DbMaintenance.IsAnyTable(MasterRunTableName, false))
        {
            return;
        }

        // SQLite exists only in the unit-test harness.  The real conversion command
        // deliberately supports MySQL/MariaDB and PostgreSQL only.
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            return;
        }

        var layout = ReadRunDateMigrationLayout();
        if (layout.State != RunDateMigrationState.Completed)
        {
            throw new InvalidOperationException(
                "surf_runs.Date is not yet the required Unix-milliseconds BIGINT. " +
                "Stop writers, take a verified backup, and run the dedicated convert-run-dates command before backend migration or normal CodeFirst startup.");
        }

        if (layout.DateColumn!.IsNullable)
        {
            throw new InvalidOperationException(
                "surf_runs.Date is a nullable BIGINT. Rerun convert-run-dates so it can verify rows and restore the master NOT NULL invariant.");
        }
    }

    /// <summary>
    /// Converts the historical DateTime storage convention into a Unix timestamp
    /// without allowing an unspecified database materialization to be interpreted as
    /// the API host's local time.  Master writes used UTC; Unspecified therefore means
    /// the same UTC clock value, while explicitly Local values are normalized first.
    /// </summary>
    internal static long ToUnixTimeMilliseconds(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    internal static DateTime FromUnixTimeMilliseconds(long value)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidOperationException(
                $"surf_runs.Date contains {value}, which is outside the valid Unix-milliseconds DateTime range.", exception);
        }
    }

    private void EnsureRunDateMigrationProvider()
    {
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            throw new NotSupportedException(
                "surf_runs.Date conversion supports only MySQL/MariaDB and PostgreSQL; LiteDB and test-only SQLite are not converted.");
        }
    }

    private async Task<long> CountMasterRunRowsAsync()
        => await _db.Queryable<MasterRunDateCurrentRow>().CountAsync();

    private RunDateMigrationLayout ReadRunDateMigrationLayout()
    {
        var columns = _db.DbMaintenance.GetColumnInfosByTableName(MasterRunTableName, false);
        ValidatePublishedMasterRunSchema(columns);

        var date = FindColumn(columns, MasterRunDateColumnName);
        var unixMigration = FindColumn(columns, RunDateUnixMillisecondsMigrationColumnName);
        var legacyBackup = FindColumn(columns, RunDateDateTimeBackupColumnName);

        if (date is not null && IsBigIntColumn(date) && unixMigration is null && legacyBackup is null)
        {
            return new RunDateMigrationLayout(RunDateMigrationState.Completed, date, null, null);
        }

        if (date is not null && IsTemporalColumn(date) && unixMigration is null && legacyBackup is null)
        {
            return new RunDateMigrationLayout(RunDateMigrationState.LegacyDateTime, date, null, null);
        }

        if (date is not null && IsTemporalColumn(date)
                         && unixMigration is not null && IsBigIntColumn(unixMigration)
                         && legacyBackup is null)
        {
            return new RunDateMigrationLayout(RunDateMigrationState.Backfilling, date, unixMigration, null);
        }

        if (date is null
            && unixMigration is not null && IsBigIntColumn(unixMigration)
            && legacyBackup is not null && IsTemporalColumn(legacyBackup))
        {
            return new RunDateMigrationLayout(RunDateMigrationState.LegacyRenamed, null, unixMigration, legacyBackup);
        }

        if (date is not null && IsBigIntColumn(date)
                         && unixMigration is null
                         && legacyBackup is not null && IsTemporalColumn(legacyBackup))
        {
            return new RunDateMigrationLayout(RunDateMigrationState.NewDateAwaitingCleanup, date, null, legacyBackup);
        }

        var dateType = date?.DataType ?? "missing";
        var temporaryType = unixMigration?.DataType ?? "missing";
        var backupType = legacyBackup?.DataType ?? "missing";
        throw new InvalidOperationException(
            "surf_runs does not match a safe published-master Date conversion state " +
            $"(Date={dateType}, temporary={temporaryType}, backup={backupType}). " +
            "Restore the verified backup or inspect the interrupted migration before retrying.");
    }

    private static void ValidatePublishedMasterRunSchema(List<DbColumnInfo> columns)
    {
        var names = new HashSet<string>(columns.Select(column => column.DbColumnName), StringComparer.OrdinalIgnoreCase);
        var expected = new HashSet<string>(MasterRunSchemaColumns, StringComparer.OrdinalIgnoreCase);
        expected.Add(RunDateUnixMillisecondsMigrationColumnName);
        expected.Add(RunDateDateTimeBackupColumnName);

        foreach (var required in MasterRunSchemaColumns.Where(column => !string.Equals(column, MasterRunDateColumnName, StringComparison.Ordinal)))
        {
            if (!names.Contains(required))
            {
                throw new InvalidOperationException(
                    $"surf_runs is not the published master schema: required column '{required}' is missing.");
            }
        }

        if (!names.Contains(MasterRunDateColumnName)
            && !names.Contains(RunDateDateTimeBackupColumnName))
        {
            throw new InvalidOperationException(
                "surf_runs is not a resumable published-master Date conversion: neither Date nor its migration backup column exists.");
        }

        var unexpected = names.Where(name => !expected.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (unexpected.Length != 0)
        {
            throw new InvalidOperationException(
                "surf_runs is not exactly the published master schema (unexpected columns: " +
                string.Join(", ", unexpected) + "). Refusing to reinterpret a different run schema.");
        }
    }

    private void AddRunDateMigrationColumn()
    {
        if (!_db.DbMaintenance.AddColumn(MasterRunTableName, new DbColumnInfo
            {
                DbColumnName = RunDateUnixMillisecondsMigrationColumnName,
                DataType = "bigint",
                IsNullable = true,
            }))
        {
            throw new InvalidOperationException("Could not add the resumable surf_runs.Date Unix-milliseconds migration column.");
        }

        _logger.LogInformation("Added temporary {Column} to {Table} for resumable Date conversion.",
                               RunDateUnixMillisecondsMigrationColumnName, MasterRunTableName);
    }

    private async Task BackfillRunDateUnixMillisecondsAsync(long rowsBefore)
    {
        // Keyset paging: the temporary column is unindexed, so filtering on NULL alone makes
        // every batch re-walk all previously converted rows (quadratic on large tables).
        // The NULL filter still lets a resumed conversion skip rows it already wrote.
        ulong? lastId = null;
        while (true)
        {
            var query = _db.Queryable<MasterRunDateLegacyRow>()
                           .Where(row => row.UnixMilliseconds == null);
            if (lastId.HasValue)
            {
                query = query.Where(row => row.Id > lastId.Value);
            }

            var batch = await query.OrderBy(row => row.Id)
                                   .Take(RunDateMigrationBatchSize)
                                   .ToListAsync();
            if (batch.Count == 0)
            {
                break;
            }

            lastId = batch[^1].Id;

            foreach (var row in batch)
            {
                if (row.LegacyDate is not { } legacyDate)
                {
                    throw new InvalidOperationException(
                        $"surf_runs.Id={row.Id} has a NULL Date. The master schema invariant is broken; restore or repair it before conversion.");
                }

                row.UnixMilliseconds = ToUnixTimeMilliseconds(legacyDate);
            }

            var updated = await _db.Updateable(batch)
                                   .UpdateColumns(row => new { row.UnixMilliseconds })
                                   .ExecuteCommandAsync();
            if (updated != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Date conversion updated {updated} rows from a {batch.Count}-row batch. " +
                    "Another writer may be active; leave the temporary column in place, stop writers, and rerun the conversion.");
            }
        }

        var rowsAfter = await CountMasterRunRowsAsync();
        if (rowsAfter != rowsBefore)
        {
            throw new InvalidOperationException(
                $"surf_runs changed from {rowsBefore} to {rowsAfter} rows while converting Date. " +
                "The column has not been swapped; stop every writer and rerun the resumable conversion.");
        }
    }

    private Task VerifyDateAndTemporaryRunDateRowsAsync(long expectedRows)
        => VerifyRunDateRowsAsync(expectedRows,
                                  ReadDateAndTemporaryRunDateBatchAsync,
                                  row => row.Id,
                                  row => row.LegacyDate,
                                  row => row.UnixMilliseconds,
                                  "the Date/temporary-column backfill");

    private Task VerifyBackupAndTemporaryRunDateRowsAsync(long expectedRows)
        => VerifyRunDateRowsAsync(expectedRows,
                                  ReadBackupAndTemporaryRunDateBatchAsync,
                                  row => row.Id,
                                  row => row.LegacyDate,
                                  row => row.UnixMilliseconds,
                                  "the legacy-renamed temporary-column state");

    private Task VerifyBackupAndPromotedRunDateRowsAsync(long expectedRows)
        => VerifyRunDateRowsAsync(expectedRows,
                                  ReadBackupAndPromotedRunDateBatchAsync,
                                  row => row.Id,
                                  row => row.LegacyDate,
                                  row => row.UnixMilliseconds,
                                  "the promoted Date/backup-column state");

    private async Task VerifyRunDateRowsAsync<T>(long                      expectedRows,
                                                   Func<ulong?, Task<List<T>>> readBatch,
                                                   Func<T, ulong>             getId,
                                                   Func<T, DateTime?>         getLegacyDate,
                                                   Func<T, long?>             getUnixMilliseconds,
                                                   string                     stateDescription)
    {
        var verifiedRows = 0L;
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
                if (getLegacyDate(row) is not { } legacyDate || getUnixMilliseconds(row) is not { } unixMilliseconds)
                {
                    throw new InvalidOperationException(
                        $"surf_runs.Id={id} lacks a Date value during {stateDescription} verification.");
                }

                var expectedUnixMilliseconds = ToUnixTimeMilliseconds(legacyDate);
                if (unixMilliseconds != expectedUnixMilliseconds)
                {
                    throw new InvalidOperationException(
                        $"surf_runs.Id={id} Date conversion mismatch ({unixMilliseconds} != {expectedUnixMilliseconds}). " +
                        "The legacy Date backup has been retained; inspect it before retrying.");
                }
            }

            verifiedRows += batch.Count;
            lastId = getId(batch[^1]);
        }

        if (verifiedRows != expectedRows || await CountMasterRunRowsAsync() != expectedRows)
        {
            throw new InvalidOperationException(
                $"surf_runs changed while {stateDescription} was being verified. The schema was not swapped; stop writers and rerun.");
        }
    }

    private async Task<List<MasterRunDateLegacyRow>> ReadDateAndTemporaryRunDateBatchAsync(ulong? lastId)
    {
        var query = _db.Queryable<MasterRunDateLegacyRow>().OrderBy(row => row.Id);
        if (lastId.HasValue)
        {
            query = query.Where(row => row.Id > lastId.Value);
        }

        return await query.Take(RunDateMigrationBatchSize).ToListAsync();
    }

    private async Task<List<MasterRunDateBackupTemporaryVerificationRow>> ReadBackupAndTemporaryRunDateBatchAsync(ulong? lastId)
    {
        var query = _db.Queryable<MasterRunDateBackupTemporaryVerificationRow>().OrderBy(row => row.Id);
        if (lastId.HasValue)
        {
            query = query.Where(row => row.Id > lastId.Value);
        }

        return await query.Take(RunDateMigrationBatchSize).ToListAsync();
    }

    private async Task<List<MasterRunDatePromotedVerificationRow>> ReadBackupAndPromotedRunDateBatchAsync(ulong? lastId)
    {
        var query = _db.Queryable<MasterRunDatePromotedVerificationRow>().OrderBy(row => row.Id);
        if (lastId.HasValue)
        {
            query = query.Where(row => row.Id > lastId.Value);
        }

        return await query.Take(RunDateMigrationBatchSize).ToListAsync();
    }

    private async Task VerifyCurrentUnixRunDateRowsAsync(long expectedRows)
    {
        var nullCount = await _db.Queryable<MasterRunDateCurrentRow>()
                                 .Where(row => row.UnixMilliseconds == null)
                                 .CountAsync();
        if (nullCount != 0)
        {
            throw new InvalidOperationException(
                $"surf_runs.Date contains {nullCount} NULL Unix-milliseconds values. Restore the backup or rerun the conversion.");
        }

        var verifiedRows = 0L;
        ulong? lastId = null;
        while (true)
        {
            var query = _db.Queryable<MasterRunDateCurrentRow>().OrderBy(row => row.Id);
            if (lastId.HasValue)
            {
                query = query.Where(row => row.Id > lastId.Value);
            }

            var batch = await query.Take(RunDateMigrationBatchSize).ToListAsync();
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                _ = FromUnixTimeMilliseconds(row.UnixMilliseconds!.Value);
            }

            verifiedRows += batch.Count;
            lastId = batch[^1].Id;
        }

        if (verifiedRows != expectedRows || await CountMasterRunRowsAsync() != expectedRows)
        {
            throw new InvalidOperationException(
                "surf_runs changed while its Unix-milliseconds Date values were being verified. Stop writers and rerun.");
        }
    }

    private void RenameLegacyDateToBackup(DbColumnInfo dateColumn)
    {
        if (!_db.DbMaintenance.RenameColumn(MasterRunTableName,
                                             dateColumn.DbColumnName,
                                             RunDateDateTimeBackupColumnName))
        {
            throw new InvalidOperationException("Could not rename surf_runs.Date to its migration backup column.");
        }
    }

    private void RenameUnixMigrationColumnToDate(DbColumnInfo unixMigrationColumn)
    {
        if (!_db.DbMaintenance.RenameColumn(MasterRunTableName,
                                             unixMigrationColumn.DbColumnName,
                                             MasterRunDateColumnName))
        {
            throw new InvalidOperationException("Could not promote the temporary Unix-milliseconds Date column.");
        }
    }

    private void EnsureNonNullableBigIntDateColumn(DbColumnInfo dateColumn)
    {
        dateColumn.DataType = "bigint";
        dateColumn.Length = 0;
        dateColumn.IsNullable = false;
        if (!_db.DbMaintenance.UpdateColumn(MasterRunTableName, dateColumn))
        {
            throw new InvalidOperationException("Could not restore the non-null BIGINT definition for surf_runs.Date.");
        }
    }

    private void EnsureRunDateRecentIndex(bool replaceExisting)
    {
        var exists = _db.DbMaintenance.IsAnyIndex(RunDateRecentIndexName);
        if (exists && !replaceExisting)
        {
            return;
        }

        if (exists && !_db.DbMaintenance.DropIndex(RunDateRecentIndexName, MasterRunTableName))
        {
            throw new InvalidOperationException($"Could not replace {RunDateRecentIndexName} after converting surf_runs.Date.");
        }

        // CodeFirst consumes RunEntity's typed SugarIndex metadata, including the
        // master descending Date/Id suffix. DbMaintenance.CreateIndex has no
        // typed sort-order parameter, so passing "DESC" through a column string
        // would be a raw-SQL escape hatch and is intentionally avoided.
        _db.CodeFirst.InitTables(typeof(RunEntity));
        if (!_db.DbMaintenance.IsAnyIndex(RunDateRecentIndexName))
        {
            throw new InvalidOperationException($"Could not create {RunDateRecentIndexName} after converting surf_runs.Date.");
        }
    }

    private void DropLegacyDateBackup(DbColumnInfo legacyBackupColumn)
    {
        if (!_db.DbMaintenance.DropColumn(MasterRunTableName, legacyBackupColumn.DbColumnName))
        {
            throw new InvalidOperationException(
                "Could not remove the verified temporary surf_runs Date backup column. " +
                "The new Date BIGINT remains valid; rerun convert-run-dates to resume cleanup.");
        }
    }

    private static DbColumnInfo? FindColumn(IEnumerable<DbColumnInfo> columns, string name)
        => columns.FirstOrDefault(column => string.Equals(column.DbColumnName, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsBigIntColumn(DbColumnInfo column)
    {
        var type = NormalizeColumnType(column.DataType);
        return type is "bigint" or "int8" or "long" or "int64";
    }

    private static bool IsTemporalColumn(DbColumnInfo column)
    {
        var type = NormalizeColumnType(column.DataType);
        return type is "date" or "datetime" or "datetime2" or "timestamp" or "timestamptz"
               || type.StartsWith("timestamp ", StringComparison.Ordinal)
               || type.StartsWith("datetime ", StringComparison.Ordinal);
    }

    private static string NormalizeColumnType(string? type)
    {
        var normalized = type?.Trim().ToLowerInvariant() ?? string.Empty;
        var parenthesis = normalized.IndexOf('(');
        return parenthesis >= 0 ? normalized[..parenthesis].Trim() : normalized;
    }

    private enum RunDateMigrationState
    {
        Completed,
        LegacyDateTime,
        Backfilling,
        LegacyRenamed,
        NewDateAwaitingCleanup,
    }

    private readonly record struct RunDateMigrationLayout(RunDateMigrationState State,
                                                           DbColumnInfo? DateColumn,
                                                           DbColumnInfo? UnixMigrationColumn,
                                                           DbColumnInfo? LegacyBackupColumn);

    [SugarTable(MasterRunTableName)]
    private sealed class MasterRunDateLegacyRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = MasterRunDateColumnName)]
        public DateTime? LegacyDate { get; set; }

        [SugarColumn(ColumnName = RunDateUnixMillisecondsMigrationColumnName, ColumnDataType = "bigint")]
        public long? UnixMilliseconds { get; set; }
    }

    [SugarTable(MasterRunTableName)]
    private sealed class MasterRunDateBackupTemporaryVerificationRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = RunDateDateTimeBackupColumnName)]
        public DateTime? LegacyDate { get; set; }

        [SugarColumn(ColumnName = RunDateUnixMillisecondsMigrationColumnName, ColumnDataType = "bigint")]
        public long? UnixMilliseconds { get; set; }
    }

    [SugarTable(MasterRunTableName)]
    private sealed class MasterRunDatePromotedVerificationRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = RunDateDateTimeBackupColumnName)]
        public DateTime? LegacyDate { get; set; }

        [SugarColumn(ColumnName = MasterRunDateColumnName, ColumnDataType = "bigint")]
        public long? UnixMilliseconds { get; set; }
    }

    [SugarTable(MasterRunTableName)]
    private sealed class MasterRunDateCurrentRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = MasterRunDateColumnName, ColumnDataType = "bigint")]
        public long? UnixMilliseconds { get; set; }
    }
}
