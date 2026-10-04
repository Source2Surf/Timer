using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_runs_replay")]
[SugarIndex("idx_surf_runs_replay_map", nameof(MapId), OrderByType.Asc)]
[SugarIndex("idx_surf_runs_replay_runid", nameof(RunId), OrderByType.Asc)]
internal sealed class ReplayEntity : BaseSteamIdEntity
{
    [SugarColumn(IsPrimaryKey = true)]
    public ulong MapId { get; set; }

    [SugarColumn(IsPrimaryKey = true)]
    public ulong RunId { get; set; }

    public string Replay { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
