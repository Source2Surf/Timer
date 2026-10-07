using System;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.Backend.Storage;

// Dates people and tools read are unix milliseconds, UTC, like runs' and maps'. Older databases kept these as SQL
// timestamps, which say nothing of their time zone: each start moves any it still has into the new columns (which
// CodeFirst added, at 0) in key order and batches, as run dates were converted, then drops them.
internal sealed partial class StorageServiceImpl
{
    private const int DateMigrationBatchSize = 1_000;

    internal void MigrateLegacyDates()
    {
        MigrateDateColumn<PlayerUpdatedRow>("surf_players", "UpdatedAt", "Id", x => x.Id, x => x.Old, (x, ms) => x.New = ms, x => x.New);
        MigrateDateColumn<PlayerJoinedRow>("surf_players", "JoinedAtUtc", "Id", x => x.Id, x => x.Old, (x, ms) => x.New = ms, x => x.New);

        // A player without a join date (master never kept one, older writers left it empty) joined when last updated.
        var joined = _db.Updateable<PlayerEntity>()
                        .SetColumns(x => x.JoinedAtUnixMilliseconds == x.UpdatedAtUnixMilliseconds)
                        .Where(x => x.JoinedAtUnixMilliseconds == 0)
                        .ExecuteCommand();

        if (joined > 0)
        {
            _logger.LogInformation("Initialized {Count} player join dates from their last update; first-join times are not recoverable.", joined);
        }

        MigrateDateColumn<BestRunUpdatedRow>("surf_player_best_runs", "UpdatedAt", "Id", x => x.Id, x => x.Old, (x, ms) => x.New = ms, x => x.New);
        MigrateDateColumn<TrackScoreUpdatedRow>("surf_player_track_scores", "UpdatedAt", "Id", x => x.Id, x => x.Old, (x, ms) => x.New = ms, x => x.New);
        MigrateDateColumn<ReplayCreatedRow>("surf_runs_replay", "CreatedAt", "RunId", x => x.RunId, x => x.Old, (x, ms) => x.New = ms, x => x.New);
        MigrateDateColumn<ReplayUpdatedRow>("surf_runs_replay", "UpdatedAt", "RunId", x => x.RunId, x => x.Old, (x, ms) => x.New = ms, x => x.New);
        MigrateDateColumn<SegmentDateRow>("surf_runs_segments", "Date", "Id", x => x.Id, x => x.Old, (x, ms) => x.New = ms, x => x.New);
    }

    private void MigrateDateColumn<T>(string                  table,
                                      string                  column,
                                      string                  keyColumn,
                                      Func<T, ulong>          key,
                                      Func<T, DateTime?>      old,
                                      Action<T, long>         set,
                                      Expression<Func<T, object>> newColumn)
        where T : class, new()
    {
        if (!_db.DbMaintenance.IsAnyTable(table, false) || !_db.DbMaintenance.IsAnyColumn(table, column, false))
        {
            return;
        }

        ulong? last  = null;
        var    moved = 0;

        while (true)
        {
            var query = _db.Queryable<T>();

            if (last is { } after)
            {
                query = query.Where($"{keyColumn} > @after", new { after });
            }

            var batch = query.OrderBy(keyColumn).Take(DateMigrationBatchSize).ToList();

            if (batch.Count == 0)
            {
                break;
            }

            last = key(batch[^1]);

            foreach (var row in batch)
            {
                if (old(row) is { } date)
                {
                    set(row, ToUnixTimeMilliseconds(date));
                }
            }

            moved += _db.Updateable(batch).UpdateColumns(newColumn).ExecuteCommand();
        }

        if (!_db.DbMaintenance.DropColumn(table, column))
        {
            throw new InvalidOperationException($"Moved {table}.{column} to unix milliseconds but could not drop it.");
        }

        _logger.LogInformation("Moved {Rows} {Table}.{Column} values to unix milliseconds and dropped the column", moved, table, column);
    }

    [SugarTable("surf_players")]
    private sealed class PlayerUpdatedRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "UpdatedAt")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(PlayerEntity.UpdatedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_players")]
    private sealed class PlayerJoinedRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "JoinedAtUtc")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(PlayerEntity.JoinedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_player_best_runs")]
    private sealed class BestRunUpdatedRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "UpdatedAt")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(PlayerBestRunEntity.UpdatedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_player_track_scores")]
    private sealed class TrackScoreUpdatedRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "UpdatedAt")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(PlayerTrackScoreEntity.UpdatedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_runs_replay")]
    private sealed class ReplayCreatedRow
    {
        [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        [SugarColumn(IsPrimaryKey = true)]
        public ulong MapId { get; set; }

        [SugarColumn(IsPrimaryKey = true)]
        public ulong RunId { get; set; }

        [SugarColumn(ColumnName = "CreatedAt")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(ReplayEntity.CreatedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_runs_replay")]
    private sealed class ReplayUpdatedRow
    {
        [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")]
        public long SteamId { get; set; }

        [SugarColumn(IsPrimaryKey = true)]
        public ulong MapId { get; set; }

        [SugarColumn(IsPrimaryKey = true)]
        public ulong RunId { get; set; }

        [SugarColumn(ColumnName = "UpdatedAt")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(ReplayEntity.UpdatedAtUnixMilliseconds))]
        public long New { get; set; }
    }

    [SugarTable("surf_runs_segments")]
    private sealed class SegmentDateRow
    {
        [SugarColumn(IsPrimaryKey = true)]
        public ulong Id { get; set; }

        [SugarColumn(ColumnName = "Date")]
        public DateTime? Old { get; set; }

        [SugarColumn(ColumnName = nameof(RunSegmentEntity.DateUnixMilliseconds))]
        public long New { get; set; }
    }
}
