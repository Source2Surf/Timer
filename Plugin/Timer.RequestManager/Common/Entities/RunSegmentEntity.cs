using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_runs_segments")]
[SugarIndex("idx_surf_runs_segments_runid_stage", nameof(RunId), OrderByType.Asc, nameof(Stage), OrderByType.Asc)]
internal sealed class RunSegmentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    public ulong  RunId   { get; set; }
    public ushort Stage   { get; set; }
    public float  Time    { get; set; }
    public uint   Jumps   { get; set; }
    public uint   Strafes { get; set; }
    public float  Sync    { get; set; }

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

    public DateTime Date { get; set; }
}
