using System;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_player_best_runs")]
[SugarIndex("idx_player_best_runs_unique",
            nameof(SteamId), OrderByType.Asc,
            nameof(MapId), OrderByType.Asc,
            nameof(RunType), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            nameof(Stage), OrderByType.Asc,
            true)]
// Boards order by (BestTime, RunId); with RunId in the index they are read in order instead of sorted.
[SugarIndex("idx_player_best_runs_board",
            nameof(MapId), OrderByType.Asc,
            nameof(RunType), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            nameof(Stage), OrderByType.Asc,
            nameof(BestTime), OrderByType.Asc,
            nameof(RunId), OrderByType.Asc,
            nameof(SteamId), OrderByType.Asc)]
internal sealed class PlayerBestRunEntity : BaseSteamIdSerialEntity
{
    public ulong   MapId    { get; set; }
    public RunType RunType  { get; set; }
    public ushort  Stage    { get; set; }
    public int     Style    { get; set; }
    public ushort  Track    { get; set; }
    public ulong   RunId    { get; set; }
    public float   BestTime { get; set; }

    [SugarColumn(DefaultValue = "0")]
    public int BestTicks { get; set; }
    public DateTime UpdatedAt { get; set; }
}
