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

        var noPendingTimestamp = new DateTime(9999, 1, 1);
        var aggregate = await _db.Queryable<ScoreRecalcOutboxEntity>()
            .Select(x => new WorkerHealthAggregate
            {
                PendingCount = SqlFunc.AggregateSum(SqlFunc.IIF(
                    x.DeadLetteredAtUtc == null && x.RequestedGeneration > x.ProcessedGeneration, 1, 0)),
                DeadLetterCount = SqlFunc.AggregateSum(SqlFunc.IIF(
                    x.DeadLetteredAtUtc != null, 1, 0)),
                // PostgreSQL cannot infer the type of a bare NULL parameter in a DateTime CASE.
                // A provider-safe future date is outside the valid pending-age domain and maps
                // back to null after the aggregate, while preserving one typed aggregate query.
                OldestPendingSinceUtc = SqlFunc.AggregateMin(SqlFunc.IIF(
                    x.DeadLetteredAtUtc == null && x.RequestedGeneration > x.ProcessedGeneration,
                    SqlFunc.IsNull(x.PendingSinceUtc, x.CreatedAtUtc), noPendingTimestamp)),
            })
            .FirstAsync(OperationCancellation);

        var oldestPendingSinceUtc = aggregate?.OldestPendingSinceUtc;
        if (oldestPendingSinceUtc == noPendingTimestamp) oldestPendingSinceUtc = null;

        return new TimerBackendWorkerHealth
        {
            Enabled = true,
            PendingCount = aggregate?.PendingCount ?? 0,
            DeadLetterCount = aggregate?.DeadLetterCount ?? 0,
            OldestPendingSinceUtc = oldestPendingSinceUtc is { } value
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null,
            StartedAtUtc = _workerStartedAtUtc,
            LastSuccessfulScanUtc = _scoreRecalcScheduler.LastSuccessfulScanUtc,
        };
    }

    private sealed class WorkerHealthAggregate
    {
        public int? PendingCount { get; set; }
        public int? DeadLetterCount { get; set; }
        public DateTime? OldestPendingSinceUtc { get; set; }
    }
}
