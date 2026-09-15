using System;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
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
}
