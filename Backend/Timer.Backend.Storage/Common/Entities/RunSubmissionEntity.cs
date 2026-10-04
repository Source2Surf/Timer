using System;
using SqlSugar;

namespace Source2Surf.Timer.Common.Entities;

/// <summary>
/// Durable idempotency inbox for backend-originated run submissions. A row is reserved
/// before the run is written and receives its stable result in the same transaction.
/// </summary>
[SugarTable("surf_run_submissions")]
[SugarIndex("idx_surf_run_submissions_submission_unique",
            nameof(SubmissionId), OrderByType.Asc,
            true)]
internal sealed class RunSubmissionEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public ulong Id { get; set; }

    // Canonical lower-case Guid "N" representation. Keeping this as text makes the
    // global uniqueness semantics identical on the supported SQL dialects.
    [SugarColumn(Length = 32, IsNullable = false)]
    public string SubmissionId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string PayloadHash { get; set; } = string.Empty;

    public int HashVersion { get; set; }

    public int ContractVersion { get; set; }

    public int RulesetVersion { get; set; }

    // Zero is only observable inside the transaction while the inbox row is reserved.
    public ulong RunId { get; set; }

    public int AttemptResult { get; set; }

    public byte RankState { get; set; }

    public int Rank { get; set; }

    public DateTime FinishedAtUtc { get; set; }

    public DateTime ReceivedAtUtc { get; set; }
}
