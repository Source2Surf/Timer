using System;

namespace Source2Surf.Timer.Backend.Contracts;

public sealed class WorkerHealthStatusDto
{
    public string Status { get; init; } = string.Empty;
    public int PendingCount { get; init; }
    public int DeadLetterCount { get; init; }
    public double OldestPendingAgeSeconds { get; init; }
    public DateTime? LastSuccessfulScanUtc { get; init; }
}
