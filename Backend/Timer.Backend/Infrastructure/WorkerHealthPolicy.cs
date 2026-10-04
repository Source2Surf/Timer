using System;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Storage;

namespace Timer.Backend.Infrastructure;

internal static class WorkerHealthPolicy
{
    internal static WorkerHealthStatusDto Evaluate(TimerBackendWorkerHealth health, TimeSpan maxLag, DateTime nowUtc)
    {
        var pendingAge = health.OldestPendingSinceUtc is { } oldest
            ? Math.Max(0, (nowUtc - oldest).TotalSeconds) : 0;
        var lastScan = health.LastSuccessfulScanUtc ?? health.StartedAtUtc ?? nowUtc;
        var unhealthy = health.DeadLetterCount > 0 || pendingAge > maxLag.TotalSeconds
                        || nowUtc - lastScan > maxLag;
        return new WorkerHealthStatusDto
        {
            Status = !health.Enabled ? "disabled" : unhealthy ? "degraded" : "healthy",
            PendingCount = health.PendingCount,
            DeadLetterCount = health.DeadLetterCount,
            OldestPendingAgeSeconds = pendingAge,
            LastSuccessfulScanUtc = health.LastSuccessfulScanUtc,
        };
    }
}
