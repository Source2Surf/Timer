using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

// A player's settings (HUD, !hide, sounds) in the game server's compact binary format; storage never parses them.
[SugarTable("surf_player_settings")]
internal sealed class PlayerSettingsEntity : BaseSteamIdEntity
{
    public byte[] Data { get; set; } = [];

    [SugarColumn(ColumnDataType = "bigint")]
    public long UpdatedAtUnixMilliseconds { get; set; }
}
