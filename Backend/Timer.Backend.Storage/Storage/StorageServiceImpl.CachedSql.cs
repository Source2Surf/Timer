using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private readonly ConcurrentDictionary<string, CachedSql> _cachedSql = new(StringComparer.Ordinal);

    // Arguments while SqlSugar generates a shape's SQL. No real value comes near them.
    private static class Sentinel
    {
        public const ulong  MapId   = 9_100_000_000_000_001;
        public const long   SteamId = 9_100_000_000_000_002;
        public const int    Style   = 2_100_000_003;
        public const ushort Track   = 64_004;
        public const ushort Stage   = 64_005;
        public const uint   Points  = 4_100_000_006;
        public const ulong  RunId   = 9_100_000_000_000_007;
        public const double Factor  = 9_100_008.5;

        public static readonly DateTime Now   = new(2099, 1, 2, 3, 4, 9, DateTimeKind.Utc);
        public static readonly DateTime Later = new(2099, 1, 2, 3, 4, 10, DateTimeKind.Utc);
    }

    private CachedSql CachedShape(string shape, Func<KeyValuePair<string, List<SugarParameter>>> generate, params object[] sentinels)
        => _cachedSql.TryGetValue(shape, out var cached)
               ? cached
               : _cachedSql.GetOrAdd(shape, CachedSql.Create(generate(), sentinels));

    // Runs through SqlSugar's Ado, so logging, errors, cancellation and the operation's connection still apply.
    private async Task<DbDataReader> ReadAsync(CachedSql read, params object[] arguments)
    {
        _db.Ado.CancellationToken = OperationCancellation;

        return (DbDataReader) await _db.Ado.GetDataReaderAsync(read.Sql, read.Bind(arguments));
    }

    private Task<int> ExecuteAsync(CachedSql statement, params object[] arguments)
    {
        _db.Ado.CancellationToken = OperationCancellation;

        return _db.Ado.ExecuteCommandAsync(statement.Sql, statement.Bind(arguments));
    }

    // Shapes carry their LIMIT, and HTTP callers choose any limit: read one of a few sizes and stop
    // at the requested count, so the shape cache stays bounded.
    private static int ShapeLimit(int limit)
        => limit switch
        {
            <= 10   => 10,
            <= 50   => 50,
            <= 100  => 100,
            <= 500  => 500,
            <= 1000 => 1000,
            _       => IRequestManager.DefaultRecordLimit,
        };

    private async Task<IReadOnlyList<RunRecord>> ReadRunRecordsAsync(CachedSql read, int take, params object[] arguments)
    {
        await using var reader = await ReadAsync(read, arguments);
        var ordinals = read.Ordinals(reader, BoardColumns);
        var result   = new List<RunRecord>();

        while (result.Count < take && await reader.ReadAsync(OperationCancellation))
        {
            result.Add(ReadRunRecord(reader, ordinals));
        }

        return result;
    }

    private static readonly string[] BoardColumns =
    [
        nameof(BoardRow.Id), nameof(BoardRow.DateUnixTimeMilliseconds), nameof(BoardRow.SteamId), nameof(BoardRow.PlayerName),
        nameof(BoardRow.MapId), nameof(BoardRow.Style), nameof(BoardRow.Track), nameof(BoardRow.Stage), nameof(BoardRow.Time),
        nameof(BoardRow.Jumps), nameof(BoardRow.Strafes), nameof(BoardRow.Sync),
        nameof(BoardRow.VelocityStartX), nameof(BoardRow.VelocityStartY), nameof(BoardRow.VelocityStartZ),
        nameof(BoardRow.VelocityAvgX), nameof(BoardRow.VelocityAvgY), nameof(BoardRow.VelocityAvgZ),
        nameof(BoardRow.VelocityEndX), nameof(BoardRow.VelocityEndY), nameof(BoardRow.VelocityEndZ),
        nameof(BoardRow.Ticks),
    ];

    // Integers are read as Int64 whatever their column type, which every provider widens to.
    private static RunRecord ReadRunRecord(DbDataReader reader, int[] o)
        => new ()
        {
            Id             = reader.GetInt64(o[0]),
            RunDate        = FromUnixTimeMilliseconds(reader.GetInt64(o[1])),
            SteamId        = unchecked((ulong)reader.GetInt64(o[2])),
            PlayerName     = reader.IsDBNull(o[3]) ? string.Empty : reader.GetString(o[3]),
            MapId          = unchecked((ulong)reader.GetInt64(o[4])),
            Style          = (int)reader.GetInt64(o[5]),
            Track          = (int)reader.GetInt64(o[6]),
            Stage          = (int)reader.GetInt64(o[7]),
            Time           = TimeOf(reader.GetInt64(o[21]), reader.GetFloat(o[8])),
            Jumps          = (int)Math.Min(reader.GetInt64(o[9]), int.MaxValue),
            Strafes        = (int)Math.Min(reader.GetInt64(o[10]), int.MaxValue),
            Sync           = reader.GetFloat(o[11]),
            VelocityStartX = reader.GetFloat(o[12]),
            VelocityStartY = reader.GetFloat(o[13]),
            VelocityStartZ = reader.GetFloat(o[14]),
            VelocityAvgX   = reader.GetFloat(o[15]),
            VelocityAvgY   = reader.GetFloat(o[16]),
            VelocityAvgZ   = reader.GetFloat(o[17]),
            VelocityEndX   = reader.GetFloat(o[18]),
            VelocityEndY   = reader.GetFloat(o[19]),
            VelocityEndZ   = reader.GetFloat(o[20]),
        };
}
