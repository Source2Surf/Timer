using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MySqlConnector;
using Npgsql;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Names alone are not a correctness guarantee: an operator can recreate an index
    // under the same name with different columns, or without UNIQUE.
    private static readonly RequiredWriteIndex[] RequiredWriteIndexes =
    [
        new("surf_maps", "idx_surf_maps_file", true, ["File"]),
        new("surf_players", "idx_surf_players_steamid", true, ["SteamId"]),
        new("surf_player_best_runs", "idx_player_best_runs_unique", true,
            ["SteamId", "MapId", "RunType", "Style", "Track", "Stage"]),
        new("surf_player_track_scores", "idx_player_track_scores_steam_map_style_track", true,
            ["SteamId", "MapId", "Style", "Track"]),
        new("surf_run_submissions", "idx_surf_run_submissions_submission_unique", true, ["SubmissionId"]),
        new("surf_score_recalc_outbox", "idx_score_recalc_outbox_unique", true, ["MapId", "Style", "Track"]),
        new("surf_score_recalc_outbox", "idx_score_recalc_outbox_pending", false,
            ["DeadLetteredAtUtc", "AvailableAtUtc", "Id", "LeaseUntilUtc"]),
        new("surf_player_track_scores", "idx_player_track_scores_map_style_track", false,
            ["MapId", "Style", "Track", "SteamId", "Points"]),
    ];

    private async Task ValidateWriteIndexesAsync()
    {
        var names = RequiredWriteIndexes.Select(x => x.Name).ToArray();
        var actual = _db.CurrentConnectionConfig.DbType switch
        {
            DbType.MySql => await ReadMySqlIndexesAsync(names),
            DbType.PostgreSQL => await ReadPostgreSqlIndexesAsync(names),
            DbType.Sqlite => await ReadSqliteIndexesAsync(names),
            _ => throw new NotSupportedException("Write index validation supports MySQL/MariaDB and PostgreSQL."),
        };

        foreach (var required in RequiredWriteIndexes)
        {
            if (!actual.Any(index => index.Usable
                && string.Equals(index.Name, required.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(index.Table, required.Table, StringComparison.OrdinalIgnoreCase)
                && index.Unique == required.Unique
                && index.Columns.SequenceEqual(required.Columns, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Timer backend write schema is not ready; index '{required.Name}' on '{required.Table}' " +
                    $"must be a valid, unfiltered {(required.Unique ? "unique " : "")}index on " +
                    $"({string.Join(", ", required.Columns)}). Verify the database migration before starting writers.");
            }
        }
    }

    private async Task<List<WriteIndexDefinition>> ReadMySqlIndexesAsync(string[] names)
    {
        var database = new MySqlConnectionStringBuilder(_db.CurrentConnectionConfig.ConnectionString).Database;
        var rows = await _db.Queryable<MySqlIndexColumn>()
            .Where(x => x.Database == database && names.Contains(x.Name))
            .ToListAsync(OperationCancellation);
        return rows.GroupBy(x => (x.Table, x.Name))
            .Select(group => new WriteIndexDefinition(group.Key.Table, group.Key.Name,
                group.All(x => x.NonUnique == 0),
                group.All(x => x.PrefixLength == null && x.Column != null),
                group.OrderBy(x => x.Position).Select(x => x.Column ?? "").ToArray()))
            .ToList();
    }

    private async Task<List<WriteIndexDefinition>> ReadPostgreSqlIndexesAsync(string[] names)
    {
        // SqlSugar's schema maintenance targets public by default and supports a single
        // Search Path schema. Refuse ambiguous paths rather than validate another schema.
        var schema = new NpgsqlConnectionStringBuilder(_db.CurrentConnectionConfig.ConnectionString).SearchPath;
        schema = string.IsNullOrWhiteSpace(schema) ? "public" : schema.Trim();
        if (!Regex.IsMatch(schema, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            throw new InvalidOperationException("Write index validation requires a single PostgreSQL Search Path schema.");
        if (_db.CurrentConnectionConfig.MoreSettings?.PgSqlIsAutoToLowerSchema != false)
            schema = schema.ToLowerInvariant();

        var indexes = await _db.Queryable<PgIndex>()
            .InnerJoin<PgClass>((index, name) => index.IndexId == name.Id)
            .InnerJoin<PgClass>((index, name, table) => index.TableId == table.Id)
            .InnerJoin<PgNamespace>((index, name, table, ns) => table.NamespaceId == ns.Id)
            .Where((index, name, table, ns) => ns.Name == schema && names.Contains(name.Name)
                && index.Valid && index.Ready && index.Live && index.Immediate && index.Predicate == null)
            .Select((index, name, table, ns) => new PgIndexDefinition
            {
                Name = name.Name, Table = table.Name, TableId = SqlFunc.ToInt64(table.Id),
                Unique = index.Unique, KeyCount = index.KeyCount, Keys = index.Keys,
            })
            .ToListAsync(OperationCancellation);
        if (indexes.Count == 0) return [];
        var tableIds = indexes.Select(x => x.TableId).Distinct().ToArray();
        var columns = await _db.Queryable<PgAttribute>()
            .Where(x => tableIds.Contains(SqlFunc.ToInt64(x.TableId)) && x.Number > 0 && !x.Dropped)
            .Select(x => new { TableId = SqlFunc.ToInt64(x.TableId), x.Number, x.Name })
            .ToListAsync(OperationCancellation);
        var columnNames = columns.ToDictionary(x => (x.TableId, x.Number), x => x.Name);

        return indexes.Select(index => new WriteIndexDefinition(index.Table, index.Name, index.Unique,
            index.Keys.Length >= index.KeyCount && index.Keys.Take(index.KeyCount)
                .All(key => columnNames.ContainsKey((index.TableId, key))),
            index.Keys.Take(index.KeyCount)
                .Select(key => columnNames.GetValueOrDefault((index.TableId, key), "")).ToArray()))
            .ToList();
    }

    private async Task<List<WriteIndexDefinition>> ReadSqliteIndexesAsync(string[] names)
    {
        // SQLite is used only by storage unit tests. Its catalog exposes index definitions
        // as text. Parse only simple full-column definitions; never execute catalog SQL.
        var rows = await _db.Queryable<SqliteIndex>()
            .Where(x => x.Type == "index" && names.Contains(x.Name))
            .ToListAsync(OperationCancellation);
        return rows.Select(row =>
        {
            var match = Regex.Match(row.Definition ?? "",
                @"^\s*CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+[^()]+\((?<columns>[^()]*)\)\s*;?\s*$",
                RegexOptions.IgnoreCase);
            var columns = match.Success
                ? match.Groups["columns"].Value.Split(',').Select(column =>
                    Regex.Replace(column.Trim(), @"\s+(ASC|DESC)$", "", RegexOptions.IgnoreCase)
                        .Trim().Trim('"', '\x60', '[', ']')).ToArray()
                : [];
            return new WriteIndexDefinition(row.Table, row.Name, match.Groups["unique"].Success, match.Success, columns);
        }).ToList();
    }

    private sealed record RequiredWriteIndex(string Table, string Name, bool Unique, string[] Columns);
    private sealed record WriteIndexDefinition(string Table, string Name, bool Unique, bool Usable, string[] Columns);

    [SugarTable("information_schema.statistics")]
    private sealed class MySqlIndexColumn
    {
        [SugarColumn(ColumnName = "TABLE_SCHEMA")] public string Database { get; set; } = "";
        [SugarColumn(ColumnName = "TABLE_NAME")] public string Table { get; set; } = "";
        [SugarColumn(ColumnName = "INDEX_NAME")] public string Name { get; set; } = "";
        [SugarColumn(ColumnName = "COLUMN_NAME")] public string? Column { get; set; }
        [SugarColumn(ColumnName = "NON_UNIQUE")] public int NonUnique { get; set; }
        [SugarColumn(ColumnName = "SEQ_IN_INDEX")] public int Position { get; set; }
        [SugarColumn(ColumnName = "SUB_PART")] public long? PrefixLength { get; set; }
    }

    [SugarTable("pg_catalog.pg_index")]
    private sealed class PgIndex
    {
        [SugarColumn(ColumnName = "indexrelid")] public uint IndexId { get; set; }
        [SugarColumn(ColumnName = "indrelid")] public uint TableId { get; set; }
        [SugarColumn(ColumnName = "indisunique")] public bool Unique { get; set; }
        [SugarColumn(ColumnName = "indisvalid")] public bool Valid { get; set; }
        [SugarColumn(ColumnName = "indisready")] public bool Ready { get; set; }
        [SugarColumn(ColumnName = "indislive")] public bool Live { get; set; }
        [SugarColumn(ColumnName = "indimmediate")] public bool Immediate { get; set; }
        [SugarColumn(ColumnName = "indnkeyatts")] public short KeyCount { get; set; }
        [SugarColumn(ColumnName = "indkey", IsArray = true)] public short[] Keys { get; set; } = [];
        [SugarColumn(ColumnName = "indpred")] public string? Predicate { get; set; }
    }

    [SugarTable("pg_catalog.pg_class")]
    private sealed class PgClass
    {
        [SugarColumn(ColumnName = "oid")] public uint Id { get; set; }
        [SugarColumn(ColumnName = "relname")] public string Name { get; set; } = "";
        [SugarColumn(ColumnName = "relnamespace")] public uint NamespaceId { get; set; }
    }

    [SugarTable("pg_catalog.pg_namespace")]
    private sealed class PgNamespace
    {
        [SugarColumn(ColumnName = "oid")] public uint Id { get; set; }
        [SugarColumn(ColumnName = "nspname")] public string Name { get; set; } = "";
    }

    [SugarTable("pg_catalog.pg_attribute")]
    private sealed class PgAttribute
    {
        [SugarColumn(ColumnName = "attrelid")] public uint TableId { get; set; }
        [SugarColumn(ColumnName = "attnum")] public short Number { get; set; }
        [SugarColumn(ColumnName = "attname")] public string Name { get; set; } = "";
        [SugarColumn(ColumnName = "attisdropped")] public bool Dropped { get; set; }
    }

    private sealed class PgIndexDefinition
    {
        public string Name { get; set; } = "";
        public string Table { get; set; } = "";
        public long TableId { get; set; }
        public bool Unique { get; set; }
        public short KeyCount { get; set; }
        [SugarColumn(IsArray = true)] public short[] Keys { get; set; } = [];
    }

    [SugarTable("sqlite_schema")]
    private sealed class SqliteIndex
    {
        [SugarColumn(ColumnName = "type")] public string Type { get; set; } = "";
        [SugarColumn(ColumnName = "name")] public string Name { get; set; } = "";
        [SugarColumn(ColumnName = "tbl_name")] public string Table { get; set; } = "";
        [SugarColumn(ColumnName = "sql")] public string? Definition { get; set; }
    }
}
