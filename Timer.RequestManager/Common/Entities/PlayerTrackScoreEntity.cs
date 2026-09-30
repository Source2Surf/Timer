using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_player_track_scores")]
[SugarIndex("idx_player_track_scores_steam_map_style_track",
            nameof(SteamId), OrderByType.Asc,
            nameof(MapId), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            true)]  // Unique index; serves the IN(SteamId)/GROUP BY total-points aggregation.
// Supports the recalc delta-read (MapId, Style, Track), which the SteamId-leading
// unique index cannot serve. MySQL also includes the primary ID in this index;
// PostgreSQL may fetch the ID from the heap when preparing updates.
[SugarIndex("idx_player_track_scores_map_style_track",
            nameof(MapId), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            nameof(SteamId), OrderByType.Asc,
            nameof(Points), OrderByType.Asc,
            false)]
internal sealed class PlayerTrackScoreEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    [SugarColumn(ColumnDataType = "bigint")]
    public long SteamId { get; set; }

    public ulong MapId { get; set; }
    public int Style { get; set; }
    public ushort Track { get; set; }
    // See PlayerEntity.Points: a single board can exceed signed INT while still
    // remaining within the public uint score contract.
    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(UInt32BigIntConverter))]
    public uint Points { get; set; }
    public DateTime UpdatedAt { get; set; }
}
