using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_maps")]
[SugarIndex("idx_surf_maps_file", nameof(File), OrderByType.Asc, true)]
internal sealed class MapEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong MapId { get; set; }

    public string File { get; set; } = "INVALID_FILE";

    public byte Tier { get; set; }

    public ushort Stages { get; set; }

    /// <summary>
    /// Base score pool for this map.
    /// 0 means use the global default (ScoreCalculator.DefaultBasePot).
    /// </summary>
    public int BasePot { get; set; }

    // CodeFirst must be able to add these columns to a populated master database.
    // The master schema has no historical values for them, so zero is the only
    // truthful backfill value.
    [SugarColumn(DefaultValue = "0")]
    public int Bonuses { get; set; }

    [SugarColumn(DefaultValue = "0")]
    public int PlayCount { get; set; }

    [SugarColumn(DefaultValue = "0")]
    public float TotalPlayTime { get; set; }

    /// <summary>
    /// Steam Workshop item the map comes from; 0 for maps outside the workshop.
    /// </summary>
    [SugarColumn(DefaultValue = "0")]
    public ulong WorkshopId { get; set; }
}
