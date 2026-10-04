using System;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_runs")]
[SugarIndex("idx_surf_runs_map_style_track_time",
            nameof(MapId),
            OrderByType.Asc,
            nameof(RunType),
            OrderByType.Asc,
            nameof(Style),
            OrderByType.Asc,
            nameof(Track),
            OrderByType.Asc,
            nameof(Stage),
            OrderByType.Asc,
            nameof(Time),
            OrderByType.Asc)]
[SugarIndex("idx_surf_runs_steam_map_style_track_time",
            nameof(SteamId),
            OrderByType.Asc,
            nameof(MapId),
            OrderByType.Asc,
            nameof(RunType),
            OrderByType.Asc,
            nameof(Style),
            OrderByType.Asc,
            nameof(Track),
            OrderByType.Asc,
            nameof(Stage),
            OrderByType.Asc,
            nameof(Time),
            OrderByType.Asc)]
[SugarIndex("idx_surf_runs_recent_main",
            nameof(MapId),
            OrderByType.Asc,
            nameof(SteamId),
            OrderByType.Asc,
            nameof(RunType),
            OrderByType.Asc,
            nameof(Stage),
            OrderByType.Asc,
            nameof(DateUnixTimeMilliseconds),
            OrderByType.Desc,
            nameof(Id),
            OrderByType.Desc)]
[SugarIndex("idx_surf_runs_rank_cover",
            nameof(MapId),
            OrderByType.Asc,
            nameof(RunType),
            OrderByType.Asc,
            nameof(Style),
            OrderByType.Asc,
            nameof(Track),
            OrderByType.Asc,
            nameof(Stage),
            OrderByType.Asc,
            nameof(Time),
            OrderByType.Asc,
            nameof(SteamId),
            OrderByType.Asc)]
internal sealed class RunEntity : BaseSteamIdSerialEntity
{
    public ulong    MapId   { get; set; }
    public RunType  RunType { get; set; }
    public ushort   Stage   { get; set; }
    public int      Style   { get; set; }
    public ushort   Track   { get; set; }
    public float    Time    { get; set; }
    public uint     Jumps   { get; set; }
    public uint     Strafes { get; set; }
    public float    Sync    { get; set; }

    public float VelocityStartX { get; set; }
    public float VelocityStartY { get; set; }
    public float VelocityStartZ { get; set; }
    public float VelocityEndX   { get; set; }
    public float VelocityEndY   { get; set; }
    public float VelocityEndZ   { get; set; }
    public float VelocityMaxX   { get; set; }
    public float VelocityMaxY   { get; set; }
    public float VelocityMaxZ   { get; set; }
    public float VelocityAvgX   { get; set; }
    public float VelocityAvgY   { get; set; }
    public float VelocityAvgZ   { get; set; }

    /// <summary>
    /// UTC Unix time in milliseconds.  The persisted master SQL column deliberately
    /// retains its historical <c>Date</c> name so existing indexes and operations keep
    /// addressing the same logical field.
    /// </summary>
    [SugarColumn(ColumnName = "Date", ColumnDataType = "bigint")]
    public long DateUnixTimeMilliseconds { get; set; }
}
