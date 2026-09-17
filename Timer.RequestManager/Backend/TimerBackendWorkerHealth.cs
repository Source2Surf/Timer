using System;

namespace Timer.RequestManager.Backend;

public sealed record TimerBackendWorkerHealth
{
    public bool Enabled { get; init; }
    public int PendingCount { get; init; }
    public int DeadLetterCount { get; init; }
    public DateTime? OldestPendingSinceUtc { get; init; }
    public DateTime? StartedAtUtc { get; init; }
    public DateTime? LastSuccessfulScanUtc { get; init; }
}
