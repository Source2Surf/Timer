using System;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.RequestManager.Backend;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    internal async Task<TimerBackendWorkerHealth> GetWorkerHealthAsync()
    {
        if (_scoreRecalcScheduler is null) return new TimerBackendWorkerHealth();
        var pendingCount = await _db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.DeadLetteredAtUtc == null && x.RequestedGeneration > x.ProcessedGeneration)
            .CountAsync(OperationCancellation);
        var deadLetterCount = await _db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.DeadLetteredAtUtc != null).CountAsync(OperationCancellation);
        var oldest = await _db.Queryable<ScoreRecalcOutboxEntity>()
            .Where(x => x.DeadLetteredAtUtc == null && x.RequestedGeneration > x.ProcessedGeneration)
            .OrderBy(x => SqlFunc.IsNull(x.PendingSinceUtc, x.CreatedAtUtc))
            .Select(x => (DateTime?)SqlFunc.IsNull(x.PendingSinceUtc, x.CreatedAtUtc))
            .FirstAsync(OperationCancellation);
        return new TimerBackendWorkerHealth
        {
            Enabled = true,
            PendingCount = pendingCount,
            DeadLetterCount = deadLetterCount,
            OldestPendingSinceUtc = oldest is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null,
            StartedAtUtc = _workerStartedAtUtc,
            LastSuccessfulScanUtc = _scoreRecalcScheduler.LastSuccessfulScanUtc,
        };
    }
}
