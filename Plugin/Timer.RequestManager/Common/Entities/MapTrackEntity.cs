using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_maps_tracks")]
internal sealed class MapTrackEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public ulong MapId { get; set; }

    [SugarColumn(IsPrimaryKey = true)]
    public ushort Track { get; set; }

    public byte Tier { get; set; }
}
