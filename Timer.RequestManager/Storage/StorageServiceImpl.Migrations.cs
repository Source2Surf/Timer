using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    /// <summary>
    /// One-shot upgrade from the master SQL schema. Unlike normal CodeFirst
    /// bootstrap, this adds targeted player/map columns and the two backend tables;
    /// it never reconciles every historical table against a newer entity model.
    /// The caller must stop old plugin instances and take a database backup first.
    /// </summary>
    internal async Task MigrateMasterDatabaseAsync()
    {
        if (_db.CurrentConnectionConfig.DbType is not (DbType.MySql or DbType.PostgreSQL))
        {
            throw new NotSupportedException("Master database migration supports only MySQL/MariaDB and PostgreSQL.");
        }

        foreach (var tableName in new[]
                 {
                     "surf_maps", "surf_maps_tracks", "surf_players", "surf_player_map_stats",
                     "surf_player_best_runs", "surf_player_track_scores", "surf_runs",
                     "surf_runs_segments", "surf_runs_replay", "surf_zones",
                 })
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                throw new InvalidOperationException(
                    $"Expected master Timer table '{tableName}' was not found. Check the target database before migrating.");
            }
        }

        RejectUnsupportedNonMasterRunSchema();
        EnsureRunDateColumnReadyForRuntime();
        await MigrateMasterPointsColumnsAsync();
        EnsurePointsColumnsReadyForRuntime();

        var mapRowsBefore = _db.Queryable<MapEntity>().Count();
        var runRowsBefore = _db.Queryable<RunEntity>().Count();

        // Map totals gained required columns since master; the join date is added separately.
        // Its three zero defaults let SQLSugar backfill populated tables before
        // tightening the columns; no historical run or date-time row is rewritten.
        BackfillNullableMapTotals();
        _db.CodeFirst.InitTables(typeof(MapEntity));
        EnsureMapTotalsColumns();
        MigratePlayerJoinDates();
        RepairInvalidStoredPlayTimes();

        MigrateReplaySteamIdColumn();
        _db.CodeFirst.InitTables(typeof(ScoreRecalcOutboxEntity), typeof(RunSubmissionEntity));

        EnsureScoreRecalcOutboxIndexes();
        EnsureRunSubmissionInboxIndex();
        EnsureTrackScoreCoveringIndex();
        foreach (var tableName in new[] { "surf_run_submissions", "surf_score_recalc_outbox" })
        {
            if (!_db.DbMaintenance.IsAnyTable(tableName, false))
            {
                throw new InvalidOperationException($"Required migration table '{tableName}' was not created.");
            }
        }

        foreach (var indexName in new[]
                 {
                     "idx_score_recalc_outbox_unique", "idx_score_recalc_outbox_pending",
                     "idx_surf_run_submissions_submission_unique",
                     "idx_player_track_scores_map_style_track",
                 })
        {
            if (!_db.DbMaintenance.IsAnyIndex(indexName))
            {
                throw new InvalidOperationException($"Required migration index '{indexName}' was not created.");
            }
        }

        await ValidateWriteIndexesAsync();

        if (_db.Queryable<MapEntity>().Count() != mapRowsBefore
            || _db.Queryable<RunEntity>().Count() != runRowsBefore)
        {
            throw new InvalidOperationException(
                "Master migration changed historical map/run row counts. Keep the old plugin stopped and inspect the database backup.");
        }

        // Populate historical best-run projections before accepting remote
        // writes. The existing seeder batches writes and skips unchanged rows;
        // running it here avoids per-board cold work on the write API.
        var mapIds = await _db.Queryable<RunEntity>()
                              .GroupBy(x => x.MapId)
                              .Select(x => x.MapId)
                              .ToListAsync();
        foreach (var mapId in mapIds)
        {
            await EnsureBestRunsSeededForMapAsync(mapId, RunType.Main);
            await EnsureBestRunsSeededForMapAsync(mapId, RunType.Stage);
        }

        _logger.LogInformation(
            "Master SQL schema migration completed ({MapRows} maps, {RunRows} historical runs retained, score columns verified as BIGINT, best-run projections seeded); run dates were converted separately by the required convert-run-dates command.",
            mapRowsBefore, runRowsBefore);
    }

    private void BackfillNullableMapTotals()
    {
        const string tableName = "surf_maps";
        var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        if (columns.Exists(x => string.Equals(x.DbColumnName, nameof(MapEntity.Bonuses), StringComparison.OrdinalIgnoreCase) && x.IsNullable))
        {
            _db.Updateable<MapTotalsMigration>().AS(tableName)
                .SetColumns(x => x.Bonuses == 0).Where(x => x.Bonuses == null).ExecuteCommand();
        }

        if (columns.Exists(x => string.Equals(x.DbColumnName, nameof(MapEntity.PlayCount), StringComparison.OrdinalIgnoreCase) && x.IsNullable))
        {
            _db.Updateable<MapTotalsMigration>().AS(tableName)
                .SetColumns(x => x.PlayCount == 0).Where(x => x.PlayCount == null).ExecuteCommand();
        }

        if (columns.Exists(x => string.Equals(x.DbColumnName, nameof(MapEntity.TotalPlayTime), StringComparison.OrdinalIgnoreCase) && x.IsNullable))
        {
            _db.Updateable<MapTotalsMigration>().AS(tableName)
                .SetColumns(x => x.TotalPlayTime == 0).Where(x => x.TotalPlayTime == null).ExecuteCommand();
        }
    }

    private void EnsureScoreRecalcOutboxIndexes()
    {
        const string tableName = "surf_score_recalc_outbox";
        if (!_db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_unique"))
        {
            _db.DbMaintenance.CreateIndex(tableName,
                [nameof(ScoreRecalcOutboxEntity.MapId), nameof(ScoreRecalcOutboxEntity.Style), nameof(ScoreRecalcOutboxEntity.Track)],
                "idx_score_recalc_outbox_unique", true);
        }

        if (!_db.DbMaintenance.IsAnyIndex("idx_score_recalc_outbox_pending"))
        {
            _db.DbMaintenance.CreateIndex(tableName,
                [nameof(ScoreRecalcOutboxEntity.DeadLetteredAtUtc), nameof(ScoreRecalcOutboxEntity.AvailableAtUtc),
                 nameof(ScoreRecalcOutboxEntity.Id), nameof(ScoreRecalcOutboxEntity.LeaseUntilUtc)],
                "idx_score_recalc_outbox_pending", false);
        }
    }

    private void EnsureMapTotalsColumns()
    {
        var columns = _db.DbMaintenance.GetColumnInfosByTableName("surf_maps", false);
        foreach (var name in new[] { nameof(MapEntity.Bonuses), nameof(MapEntity.PlayCount), nameof(MapEntity.TotalPlayTime) })
        {
            var column = columns.Find(x => string.Equals(x.DbColumnName, name, StringComparison.OrdinalIgnoreCase));
            if (column is null || column.IsNullable)
            {
                throw new InvalidOperationException($"Migration did not create required surf_maps.{name} column.");
            }
        }
    }

    private void RejectUnsupportedNonMasterRunSchema()
    {
        if ((_db.DbMaintenance.IsAnyTable("surf_runs", false)
             && (_db.DbMaintenance.IsAnyColumn("surf_runs", "ServerId", false)
                 || _db.DbMaintenance.IsAnyColumn("surf_runs", "SubmissionId", false)))
            || (_db.DbMaintenance.IsAnyTable("surf_run_submissions", false)
                && _db.DbMaintenance.IsAnyColumn("surf_run_submissions", "ServerId", false)))
        {
            throw new InvalidOperationException(
                "This migration supports the master SQL schema only; an unexpected ServerId or surf_runs.SubmissionId column was found.");
        }
    }

    private void EnsureRunSubmissionInboxIndex()
    {
        const string tableName = "surf_run_submissions";
        const string indexName = "idx_surf_run_submissions_submission_unique";

        if (!_db.DbMaintenance.IsAnyTable(tableName, false))
        {
            return;
        }

        if (!_db.DbMaintenance.IsAnyIndex(indexName))
        {
            _db.DbMaintenance.CreateIndex(tableName,
                                           [nameof(RunSubmissionEntity.SubmissionId)],
                                           indexName,
                                           true);
        }
    }

    internal void MigrateReplaySteamIdColumn(string tableName = "surf_runs_replay")
    {
        const string columnName = "SteamId";
        if (!_db.DbMaintenance.IsAnyTable(tableName, false)) return;
        var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        var column = columns.Find(x => string.Equals(x.DbColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        if (column is null || column.DataType.ToLowerInvariant() is "bigint" or "int8" or "long") return;

        try
        {
            if (_db.CurrentConnectionConfig.DbType == DbType.PostgreSQL)
            {
                // PostgreSQL cannot implicitly alter varchar to bigint. Use an
                // expression update into a typed column, then swap columns within
                // transactional DDL. Adding the column locks the table until commit;
                // any conversion error rolls back the data and schema together.
                const string temporaryColumn = "steamid_bigint_migration";
                _db.Ado.BeginTran();
                try
                {
                    _db.DbMaintenance.AddColumn(tableName, new DbColumnInfo
                    {
                        DbColumnName = temporaryColumn, DataType = "bigint", IsNullable = true,
                    });
                    _db.Updateable<ReplaySteamIdMigration>().AS(tableName)
                        .SetColumns(x => x.ConvertedSteamId == SqlFunc.ToInt64(x.SteamId))
                        .Where(x => x.ConvertedSteamId == null).ExecuteCommand();
                    // Dropping the old column removes the composite primary key;
                    // the independent MapId/RunId indexes remain in place.
                    _db.DbMaintenance.DropColumn(tableName, column.DbColumnName);
                    _db.DbMaintenance.RenameColumn(tableName, temporaryColumn, column.DbColumnName);
                    _db.DbMaintenance.UpdateColumn(tableName, new DbColumnInfo
                    {
                        DbColumnName = column.DbColumnName, DataType = "bigint", IsNullable = false,
                    });
                    _db.DbMaintenance.AddPrimaryKeys(tableName, [column.DbColumnName, "mapid", "runid"]);
                    _db.Ado.CommitTran();
                }
                catch { _db.Ado.RollbackTran(); throw; }
            }
            else
            {
                column.DataType = "bigint";
                column.Length = 0;
                column.IsNullable = false;
                _db.DbMaintenance.UpdateColumn(tableName, column);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to migrate {Table}.{Column} to BIGINT.", tableName, columnName);
            throw;
        }
    }

    private sealed class ReplaySteamIdMigration
    {
        public string SteamId { get; set; } = string.Empty;

        [SugarColumn(ColumnName = "steamid_bigint_migration")]
        public long? ConvertedSteamId { get; set; }
    }

    private sealed class MapTotalsMigration
    {
        public int? Bonuses { get; set; }

        public int? PlayCount { get; set; }

        public float? TotalPlayTime { get; set; }
    }
}
