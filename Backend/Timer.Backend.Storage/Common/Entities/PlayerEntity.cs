using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_players")]
[SugarIndex("idx_surf_players_steamid", nameof(SteamId), OrderByType.Asc, true)]
[SugarIndex("idx_surf_players_points", nameof(Points), OrderByType.Asc)]
internal sealed class PlayerEntity : BaseSteamIdSerialEntity
{
    [SugarColumn(Length = 192)]
    public string Name { get; set; } = string.Empty;

    // Scores are constrained by the CLR contract to uint, but a tier-26 board
    // legitimately exceeds a signed SQL INT.  Keep the physical representation
    // signed BIGINT across providers so MySQL/PostgreSQL can persist every uint.
    [SugarColumn(ColumnDataType = "bigint", SqlParameterDbType = typeof(UInt32BigIntConverter))]
    public uint Points { get; set; }
    public uint Runs   { get; set; }
    // Nullable only for additive upgrades and older writers. Migration freezes the
    // best existing timestamp once; new profiles always set this at creation.
    // Unix milliseconds, UTC; 0 = unknown.
    [SugarColumn(ColumnDataType = "bigint", DefaultValue = "0")]
    public long JoinedAtUnixMilliseconds { get; set; }

    [SugarColumn(ColumnDataType = "bigint", DefaultValue = "0")]
    public long UpdatedAtUnixMilliseconds { get; set; }
}
