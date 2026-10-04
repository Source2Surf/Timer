using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

[SugarTable("surf_score_recalc_outbox")]
[SugarIndex("idx_score_recalc_outbox_unique",
            nameof(MapId), OrderByType.Asc,
            nameof(Style), OrderByType.Asc,
            nameof(Track), OrderByType.Asc,
            true)]
[SugarIndex("idx_score_recalc_outbox_pending",
            nameof(DeadLetteredAtUtc), OrderByType.Asc,
            nameof(AvailableAtUtc), OrderByType.Asc,
            nameof(Id), OrderByType.Asc,
            nameof(LeaseUntilUtc), OrderByType.Asc,
            false)]
internal sealed class ScoreRecalcOutboxEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    public ulong MapId { get; set; }

    public int Style { get; set; }

    public ushort Track { get; set; }

    public long RequestedGeneration { get; set; }

    public long ProcessedGeneration { get; set; }

    // The generation of the latest request that asked to repair every board player's total
    // (recalc-scores). PB/WR requests leave it, so their recalc re-totals only changed players.
    [SugarColumn(DefaultValue = "0")]
    public long RepairGeneration { get; set; }

    public double StyleFactor { get; set; }

    /// <summary>
    /// Start of the current uninterrupted pending period. Retries, new generations and
    /// dead-letter reactivation preserve it until a successful completion. Nullable for
    /// additive upgrades; old pending rows fall back to CreatedAtUtc until completed.
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? PendingSinceUtc { get; set; }

    public DateTime AvailableAtUtc { get; set; }

    /// <summary>
    /// Consecutive delivery count. Claim increments it; a new generation or successful
    /// completion resets it, so both observed failures and crashed leases remain bounded.
    /// </summary>
    public int AttemptCount { get; set; }

    [SugarColumn(Length = 64, IsNullable = true)]
    public string? LeaseOwner { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LeaseUntilUtc { get; set; }

    [SugarColumn(Length = 2048, IsNullable = true)]
    public string? LastError { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? DeadLetteredAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
