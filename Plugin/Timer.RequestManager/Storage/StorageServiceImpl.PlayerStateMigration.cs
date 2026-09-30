using System;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    // The read API exposes integer microseconds. Stay below its Int64 boundary,
    // including float rounding when a SQL REAL value is materialized.
    internal static readonly float MaximumStoredPlayTimeSeconds =
        MathF.BitDecrement((float)(long.MaxValue / 1_000_000d));

    private static void ValidatePlayTimeDelta(float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0f || deltaSeconds > MaximumStoredPlayTimeSeconds)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds),
                "Session time must be finite, non-negative and representable as integer microseconds.");
    }

    internal void MigratePlayerJoinDates()
    {
        const string tableName = "surf_players";
        var columns = _db.DbMaintenance.GetColumnInfosByTableName(tableName, false);
        if (FindColumn(columns, nameof(PlayerEntity.JoinedAtUtc)) is null)
        {
            var source = FindColumn(columns, nameof(PlayerEntity.UpdatedAt))
                         ?? throw new InvalidOperationException("Player UpdatedAt column is missing.");
            // Add only this column; do not reconcile or drop unrelated master columns.
            if (!_db.DbMaintenance.AddColumn(tableName, new DbColumnInfo
                {
                    DbColumnName = nameof(PlayerEntity.JoinedAtUtc), DataType = source.DataType,
                    Length = source.Length, DecimalDigits = source.DecimalDigits, IsNullable = true,
                }))
                throw new InvalidOperationException("Could not add surf_players.JoinedAtUtc.");
        }

        var rows = _db.Updateable<PlayerEntity>()
                      .SetColumns(x => x.JoinedAtUtc == x.UpdatedAt)
                      .Where(x => x.JoinedAtUtc == null).ExecuteCommand();
        if (rows > 0)
            _logger.LogInformation("Initialized {Count} player join dates from existing UpdatedAt values; original first-join times are not recoverable.", rows);
    }

    internal void RepairInvalidStoredPlayTimes()
    {
        // PostgreSQL sorts NaN above finite values, so the upper bound also catches
        // NaN there. SQLite/MySQL do not store NaN through their normal float writer.
        var playerRows = _db.Updateable<PlayerMapStatsEntity>()
            .SetColumns(x => x.PlayTime == 0f)
            .Where(x => x.PlayTime < 0f || x.PlayTime > MaximumStoredPlayTimeSeconds)
            .ExecuteCommand();
        var mapRows = _db.Updateable<MapEntity>()
            .SetColumns(x => x.TotalPlayTime == 0f)
            .Where(x => x.TotalPlayTime < 0f || x.TotalPlayTime > MaximumStoredPlayTimeSeconds)
            .ExecuteCommand();
        if (playerRows + mapRows > 0)
            _logger.LogWarning("Reset invalid playtime to zero for {PlayerRows} player-map and {MapRows} map rows. Corrupt durations cannot be recovered; play counts were retained.",
                playerRows, mapRows);
    }
}