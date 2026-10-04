using System;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Zone;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_zones")]
[SugarIndex("idx_surf_zones_map_track_type_seq",
            nameof(MapId),
            OrderByType.Asc,
            nameof(Track),
            OrderByType.Asc,
            nameof(Type),
            OrderByType.Asc,
            nameof(Sequence),
            OrderByType.Asc)]
internal sealed class ZoneEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    public ulong MapId { get; set; }

    public EZoneType Type { get; set; }

    public ushort Track { get; set; }

    public ushort Sequence { get; set; }

    [SugarColumn(IsJson = true, ColumnDataType = "text")]
    public Vector Mins { get; set; }

    [SugarColumn(IsJson = true, ColumnDataType = "text")]
    public Vector Maxs { get; set; }

    [SugarColumn(IsJson = true, ColumnDataType = "text")]
    public Vector Center { get; set; }

    [SugarColumn(IsJson = true, ColumnDataType = "text")]
    public Vector Angles { get; set; }

    [SugarColumn(IsJson = true, IsNullable = true, ColumnDataType = "text")]
    public Vector? TeleportOrigin { get; set; }

    [SugarColumn(IsJson = true, IsNullable = true, ColumnDataType = "text")]
    public Vector? TeleportAngles { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "text")]
    public string? Config { get; set; }
}
