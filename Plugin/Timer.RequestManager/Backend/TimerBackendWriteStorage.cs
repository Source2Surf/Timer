using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Timer.RequestManager.Backend;

/// <summary>
/// Transport-neutral entry point for authoritative backend run writes. It deliberately
/// contains no ModSharp or SqlSugar types; the owning <see cref="TimerBackendStorage"/>
/// supplies the existing process-lifetime storage scope and lifecycle checks.
/// </summary>
public sealed class TimerBackendWriteStorage
{
    private readonly TimerBackendStorage _owner;

    public TimerBackendWriteStorage(TimerBackendStorage owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public Task<TimerBackendRunSubmissionResult> SubmitRunAsync(TimerBackendRunSubmissionCommand command, CancellationToken cancellationToken = default)
        => _owner.SubmitRunAsync(command, acceptNewWrites: true, cancellationToken);

    /// <summary>
    /// Reject a policy-obsolete new submission while still acknowledging an exact prior
    /// submission from the SQL inbox. Only the write RPC should supply this policy decision.
    /// </summary>
    public Task<TimerBackendRunSubmissionResult> SubmitRunAsync(
        TimerBackendRunSubmissionCommand command, bool acceptNewWrites, CancellationToken cancellationToken = default)
        => _owner.SubmitRunAsync(command, acceptNewWrites, cancellationToken);

    public Task<TimerBackendRunSubmissionResult?> GetSubmissionStatusAsync(Guid submissionId, CancellationToken cancellationToken = default)
        => _owner.GetSubmissionStatusAsync(submissionId, cancellationToken);

    public Task<TimerBackendPlayerProfileResult> EnsurePlayerProfileAsync(TimerBackendPlayerProfileCommand command, CancellationToken cancellationToken = default)
        => _owner.EnsurePlayerProfileAsync(command, cancellationToken);
}

/// <summary>
/// A transport-neutral player profile request, without ModSharp or SqlSugar types.
/// </summary>
public sealed class TimerBackendPlayerProfileCommand
{
    public long SteamId { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class TimerBackendPlayerProfileResult
{
    public long PlayerId { get; init; }
    public long SteamId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint Points { get; init; }
    public DateTime JoinDateUtc { get; init; }
    public DateTime LastSeenDateUtc { get; init; }
}

public enum TimerBackendRunKind : byte
{
    Main = 0,
    Stage = 1,
}

public enum TimerBackendAttemptResult : byte
{
    NoNewRecord = 0,
    NewPersonalRecord = 1,
    NewServerRecord = 2,
}

/// <summary>
/// The rank is intentionally not computed under the map lock for ordinary personal bests.
/// A caller can render a stable pending state until a later read supplies rank information.
/// </summary>
public enum TimerBackendRankState : byte
{
    NotApplicable = 0,
    Pending = 1,
    Ready = 2,
}

public enum TimerBackendSubmissionDisposition : byte
{
    Accepted = 0,
    AlreadyApplied = 1,
}

public sealed class TimerBackendMotion
{
    public float VelocityStartX { get; init; }
    public float VelocityStartY { get; init; }
    public float VelocityStartZ { get; init; }
    public float VelocityEndX { get; init; }
    public float VelocityEndY { get; init; }
    public float VelocityEndZ { get; init; }
    public float VelocityMaxX { get; init; }
    public float VelocityMaxY { get; init; }
    public float VelocityMaxZ { get; init; }
    public float VelocityAvgX { get; init; }
    public float VelocityAvgY { get; init; }
    public float VelocityAvgZ { get; init; }
}

public sealed class TimerBackendSubmissionCheckpoint
{
    public int CheckpointIndex { get; init; }
    public long TimeMicros { get; init; }
    public float Sync { get; init; }
    public TimerBackendMotion Motion { get; init; } = new ();
}

/// <summary>
/// A run submission. <see cref="StyleFactor"/> must be the trusted,
/// already-resolved server-side style multiplier; it is intentionally excluded from the
/// client payload hash so policy changes do not turn a retried submission into a conflict.
/// </summary>
public sealed class TimerBackendRunSubmissionCommand
{
    public const int CurrentContractVersion = 1;

    public int ContractVersion { get; init; } = CurrentContractVersion;
    public Guid SubmissionId { get; init; }
    public long SteamId { get; init; }
    public string MapName { get; init; } = string.Empty;
    public TimerBackendRunKind Kind { get; init; }
    public int Style { get; init; }
    public int Track { get; init; }
    public int Stage { get; init; }
    public long TimeMicros { get; init; }
    public int Jumps { get; init; }
    public int Strafes { get; init; }
    public float Sync { get; init; }
    public TimerBackendMotion Motion { get; init; } = new ();
    public IReadOnlyList<TimerBackendSubmissionCheckpoint> Checkpoints { get; init; }
        = Array.Empty<TimerBackendSubmissionCheckpoint>();
    public DateTime FinishedAtUtc { get; init; }
    public int RulesetVersion { get; init; }
    public double StyleFactor { get; init; } = 1;
}

public sealed class TimerBackendRunSubmissionResult
{
    public Guid SubmissionId { get; init; }
    public TimerBackendSubmissionDisposition Disposition { get; init; }
    public ulong RunId { get; init; }
    public TimerBackendAttemptResult AttemptResult { get; init; }
    public TimerBackendRankState RankState { get; init; }
    public int Rank { get; init; }
    public DateTime ReceivedAtUtc { get; init; }
}

public abstract class TimerBackendSubmissionException : InvalidOperationException
{
    protected TimerBackendSubmissionException(string message) : base(message) { }
}

public sealed class TimerBackendSubmissionValidationException : TimerBackendSubmissionException
{
    public TimerBackendSubmissionValidationException(string message) : base(message) { }
}

public sealed class TimerBackendMapNotFoundException : TimerBackendSubmissionException
{
    public TimerBackendMapNotFoundException(string mapName)
        : base($"Map '{mapName}' does not exist and backend submissions never create maps.") { }
}

public sealed class TimerBackendPlayerNotFoundException : TimerBackendSubmissionException
{
    public TimerBackendPlayerNotFoundException(long steamId)
        : base($"Player '{steamId}' does not exist and backend submissions never create players.") { }
}

public sealed class TimerBackendPlayerProfileValidationException : TimerBackendSubmissionException
{
    public TimerBackendPlayerProfileValidationException(string message) : base(message) { }
}

public sealed class TimerBackendSubmissionConflictException : TimerBackendSubmissionException
{
    public TimerBackendSubmissionConflictException(Guid submissionId)
        : base($"Submission '{submissionId:N}' was already applied with a different payload.") { }
}

/// <summary>
/// The database could not be reached or is temporarily refusing work (connection failures,
/// transient provider errors). Transports report it as 503 / Unavailable rather than 500.
/// </summary>
public sealed class TimerBackendUnavailableException : Exception
{
    public TimerBackendUnavailableException(Exception innerException)
        : base("The timer database is unavailable.", innerException) { }
}

public sealed class TimerBackendSubmissionPolicyException : TimerBackendSubmissionException
{
    public TimerBackendSubmissionPolicyException()
        : base("The requested write policy is not available for a new submission.") { }
}
