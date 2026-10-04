using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    // Bounded leases recover abandoned claims; map locks and generations govern completion. A
    // completed row remains as the one durable coalescing slot for its (map, style, track) key.
    private const int ScoreRecalcOutboxBatchSize = 32;
    private const int ScoreRecalcOutboxMaxAttempts = 8;
    // Match the previous in-memory scheduler's five-second coalescing window, but persist the
    // deadline so restarts and lost wake signals cannot bypass it.
    private static readonly TimeSpan ScoreRecalcDebounceDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScoreRecalcLeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Explicit durable enqueue seam for administrative callers and deterministic tests. Normal
    /// PB/WR writes use <see cref="EnqueueScoreRecalcInCurrentRecordTransactionAsync"/> so the
    /// score request commits atomically with the run and best-run projection.
    /// </summary>
    internal async Task EnqueueScoreRecalcAsync(ulong mapId, int style, ushort track, double styleFactor,
                                                DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        await WithRecordTransactionAsync(async () =>
        {
            await LockMapAsync(mapId);
            await EnqueueScoreRecalcInCurrentRecordTransactionAsync(mapId, style, track, styleFactor, now);
        });

        // The database transaction is authoritative; this is only a low-latency hint.
        WakeScoreRecalcWorker();
    }

    /// <summary>
    /// Merges a request into the current map-locked record transaction. Callers must have acquired
    /// <see cref="LockMapAsync"/> in the active ReadCommitted transaction first.
    /// </summary>
    private async Task EnqueueScoreRecalcInCurrentRecordTransactionAsync(ulong mapId, int style, ushort track,
                                                                          double styleFactor, DateTime nowUtc)
    {
        if (_db.Ado.Transaction is null)
        {
            throw new InvalidOperationException("Score-recalc outbox writes require an active record transaction.");
        }

        var availableAtUtc = nowUtc.Add(ScoreRecalcDebounceDelay);
        var merge = CachedShape("score-queue-merge", ScoreQueueMergeSql,
                                Sentinel.MapId, Sentinel.Style, Sentinel.Track, Sentinel.Factor, Sentinel.Now, Sentinel.Later);

        // The surrounding map lock serializes all foreground enqueues for this key. Keep the
        // first request's deadline while work is already pending: moving it forward on every PB
        // would be a trailing-edge debounce which can starve a busy board indefinitely. A
        // completed/dead-lettered row starts a fresh window, while a genuinely new request pulls
        // a longer failure backoff forward to at most five seconds. A valid dead letter has no
        // lease, but clear one defensively during recovery so malformed legacy state cannot keep
        // an explicit administrative requeue blocked until an unrelated lease expires. Set
        // AvailableAt and PendingSince before the generation increment because MySQL evaluates
        // single-table assignments left-to-right. Pending age survives new generations and retries.
        var updated = await ExecuteAsync(merge, mapId, style, track, styleFactor, nowUtc, availableAtUtc);

        if (updated != 0)
        {
            return;
        }

        // Map locking makes this insert race-free with other foreground enqueues. An exhausted
        // BIGINT row cannot match the guarded update, so this insert hits the unique key and
        // the enclosing transaction clearly fails rather than silently wrapping the counter.
        await _db.Insertable(new ScoreRecalcOutboxEntity
        {
            MapId = mapId,
            Style = style,
            Track = track,
            RequestedGeneration = 1,
            ProcessedGeneration = 0,
            StyleFactor = styleFactor,
            AvailableAtUtc = availableAtUtc,
            AttemptCount = 0,
            PendingSinceUtc = nowUtc,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        }).ExecuteCommandAsync(OperationCancellation);
    }

    // Generated once; arguments are bound in Sentinel order: map, style, track, factor, now, available at.
    private KeyValuePair<string, List<SugarParameter>> ScoreQueueMergeSql()
    {
        var mapId = Sentinel.MapId;
        var style = Sentinel.Style;
        var track = Sentinel.Track;
        var styleFactor = Sentinel.Factor;
        var nowUtc = Sentinel.Now;
        var availableAtUtc = Sentinel.Later;
        string? noError = null;
        DateTime? noDeadLetteredAtUtc = null;
        string? noLeaseOwner = null;
        DateTime? noLeaseUntilUtc = null;

        // (field, value) keeps this SET order (new Entity { } would not); NULLs stay on ==, which
        // writes a literal NULL where PostgreSQL rejects a text-typed null parameter.
        return _db.Updateable<ScoreRecalcOutboxEntity>()
                  .SetColumns(x => x.AvailableAtUtc,
                              x => x.DeadLetteredAtUtc == null
                                   && x.RequestedGeneration > x.ProcessedGeneration
                                   && x.AvailableAtUtc <= availableAtUtc
                                       ? x.AvailableAtUtc
                                       : availableAtUtc)
                  .SetColumns(x => x.PendingSinceUtc,
                              x => x.RequestedGeneration <= x.ProcessedGeneration
                                       ? nowUtc : SqlFunc.IsNull(x.PendingSinceUtc, x.CreatedAtUtc))
                  .SetColumns(x => x.RequestedGeneration, x => x.RequestedGeneration + 1)
                  .SetColumns(x => x.StyleFactor, x => styleFactor)
                  .SetColumns(x => x.AttemptCount, x => 0)
                  .SetColumns(x => x.LastError == noError)
                  .SetColumns(x => x.LeaseOwner, x => x.DeadLetteredAtUtc != null ? noLeaseOwner : x.LeaseOwner)
                  .SetColumns(x => x.LeaseUntilUtc, x => x.DeadLetteredAtUtc != null ? noLeaseUntilUtc : x.LeaseUntilUtc)
                  .SetColumns(x => x.DeadLetteredAtUtc == noDeadLetteredAtUtc)
                  .SetColumns(x => x.UpdatedAtUtc, x => nowUtc)
                  .Where(x => x.MapId == mapId && x.Style == style && x.Track == track
                              && x.RequestedGeneration < long.MaxValue)
                  .ToSql();
    }

    /// <summary>
    /// Processes one bounded durable batch using a fresh, unique lease token. This is intentionally
    /// internal so tests can deterministically invoke it without waiting for the scheduler scan.
    /// </summary>
    internal Task<int> ProcessScoreRecalcOutboxBatchAsync()
        => ProcessScoreRecalcOutboxBatchAsync(DateTime.UtcNow, $"score-recalc-{Guid.NewGuid():N}", () => DateTime.UtcNow);

    /// <summary>
    /// Deterministic batch-processing seam. Tests may supply a stable clock and lease owner; normal
    /// background execution uses the overload above to generate a unique owner token.
    /// </summary>
    internal async Task<int> ProcessScoreRecalcOutboxBatchAsync(DateTime nowUtc, string leaseOwner)
        => await ProcessScoreRecalcOutboxBatchAsync(nowUtc, leaseOwner, () => nowUtc);

    internal async Task<int> ProcessScoreRecalcOutboxBatchAsync(DateTime nowUtc, string leaseOwner,
                                                                Func<DateTime> completedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(leaseOwner) || leaseOwner.Length > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseOwner), "Lease owner must contain at most 64 characters.");
        }

        var candidates = await _db.Queryable<ScoreRecalcOutboxEntity>()
                                  .Where(x => x.DeadLetteredAtUtc == null
                                              && x.RequestedGeneration > x.ProcessedGeneration
                                              && x.AvailableAtUtc <= nowUtc
                                              && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= nowUtc))
                                  .OrderBy(x => x.AvailableAtUtc)
                                  .OrderBy(x => x.Id)
                                  .Take(ScoreRecalcOutboxBatchSize)
                                  .Select(x => new ScoreRecalcOutboxCandidate
                                  {
                                      Id = x.Id,
                                      AttemptCount = x.AttemptCount,
                                  })
                                  .ToListAsync(OperationCancellation);

        if (candidates.Count == 0)
        {
            return 0;
        }

        // AttemptCount is a delivery count, not only an observed exception count. A process which
        // repeatedly dies after claim therefore cannot leave poison work retrying forever. New
        // generations reset the count in the enqueue transaction.
        var exhaustedIds = candidates.Where(x => x.AttemptCount >= ScoreRecalcOutboxMaxAttempts)
                                     .Select(x => x.Id)
                                     .ToList();
        var deadLetteredCount = 0;
        if (exhaustedIds.Count > 0)
        {
            string? noLeaseOwner = null;
            DateTime? noLeaseUntilUtc = null;
            deadLetteredCount = await _db.Updateable<ScoreRecalcOutboxEntity>()
                                         .SetColumns(x => x.LeaseOwner == noLeaseOwner)
                                         .SetColumns(x => x.LeaseUntilUtc == noLeaseUntilUtc)
                                         .SetColumns(x => x.DeadLetteredAtUtc == nowUtc)
                                         .SetColumns(x => x.LastError ==
                                                          "Lease expired after the maximum delivery attempts.")
                                         .SetColumns(x => x.UpdatedAtUtc == nowUtc)
                                         .Where(x => exhaustedIds.Contains(x.Id)
                                                     && x.DeadLetteredAtUtc == null
                                                     && x.RequestedGeneration > x.ProcessedGeneration
                                                     && x.AttemptCount >= ScoreRecalcOutboxMaxAttempts
                                                     && x.AvailableAtUtc <= nowUtc
                                                     && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= nowUtc))
                                         .ExecuteCommandAsync(OperationCancellation);
            if (deadLetteredCount > 0)
            {
                _logger.LogError(
                    "Dead-lettered {Count} score-recalc Outbox request(s) after repeated expired deliveries.",
                    deadLetteredCount);
            }
        }

        var candidateIds = candidates.Where(x => x.AttemptCount < ScoreRecalcOutboxMaxAttempts)
                                     .Select(x => x.Id)
                                     .ToList();
        if (candidateIds.Count == 0)
        {
            return deadLetteredCount;
        }

        try
        {
            var processedCount = deadLetteredCount;
            foreach (var candidateId in candidateIds)
            {
                OperationCancellation.ThrowIfCancellationRequested();
                // Claim only the item we can start now. Reserving the whole batch makes a slow
                // first board consume delivery attempts for later boards that never get executed.
                // Use the current clock here: a later item needs a full lease even after a long
                // preceding recalculation.
                var claimedAtUtc = completedAtUtc();
                var leaseUntilUtc = claimedAtUtc.Add(ScoreRecalcLeaseDuration);
                var claimed = await _db.Updateable<ScoreRecalcOutboxEntity>()
                    .SetColumns(x => x.LeaseOwner == leaseOwner)
                    .SetColumns(x => x.LeaseUntilUtc == leaseUntilUtc)
                    .SetColumns(x => x.AttemptCount == x.AttemptCount + 1)
                    .SetColumns(x => x.UpdatedAtUtc == claimedAtUtc)
                    .Where(x => x.Id == candidateId
                                && x.DeadLetteredAtUtc == null
                                && x.RequestedGeneration > x.ProcessedGeneration
                                && x.AttemptCount < ScoreRecalcOutboxMaxAttempts
                                && x.AvailableAtUtc <= claimedAtUtc
                                && (x.LeaseUntilUtc == null || x.LeaseUntilUtc <= claimedAtUtc))
                    .ExecuteCommandAsync(OperationCancellation);
                if (claimed == 0) continue;

                // Re-read by our unique token. Another worker may have taken over before this
                // read. Once the map is locked, the generation fence decides whether this
                // snapshot still needs work, even if its lease subsequently expires.
                var request = await _db.Queryable<ScoreRecalcOutboxEntity>()
                    .Where(x => x.Id == candidateId
                                && x.LeaseOwner == leaseOwner
                                && x.DeadLetteredAtUtc == null
                                && x.RequestedGeneration > x.ProcessedGeneration)
                    .FirstAsync(OperationCancellation);
                if (request is null) continue;

                await ProcessLeasedScoreRecalcAsync(request, leaseOwner, completedAtUtc);
                processedCount++;
            }
            return processedCount;
        }
        finally
        {
            if (_operationCancellation.Value.IsCancellationRequested)
            {
                // Planned shutdown is not a failed delivery. Release only our remaining
                // leases with an independent, bounded cleanup token. A database outage
                // leaves the original expiry in place for recovery by another worker.
                using var cleanup = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await RunOperationAsync(() => ReleaseCancelledScoreRecalcLeasesAsync(leaseOwner), cleanup.Token);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Could not release cancelled score leases; they will expire.");
                }
            }
        }


    }

    private async Task ProcessLeasedScoreRecalcAsync(ScoreRecalcOutboxEntity request, string leaseOwner,
        Func<DateTime> completedAtUtc)
    {
        try
        {
            var recalculated = await RecalculateTrackScoresCoreAsync(
                request.MapId,
                request.Style,
                request.Track,
                request.StyleFactor,
                () => IsScoreRecalcGenerationPendingAsync(request),
                () => CompleteScoreRecalcOutboxAsync(request, completedAtUtc()));
            if (!recalculated)
            {
                // A newer generation may have arrived without stealing our lease. Release that
                // stale token promptly; if another owner already took over, this affects no rows.
                await ReleaseLeaseForNewerGenerationAsync(request, leaseOwner, completedAtUtc());
                return;
            }

        }
        catch (Exception) when (_operationCancellation.Value.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                var deadLettered = await FailScoreRecalcOutboxAsync(request, leaseOwner, completedAtUtc(), ex);
                if (deadLettered)
                {
                    _logger.LogError(ex,
                        "Score-recalc outbox request {OutboxId} for map {MapId}, style {Style}, track {Track} was dead-lettered.",
                        request.Id, request.MapId, request.Style, request.Track);
                }
                else
                {
                    _logger.LogWarning(ex,
                        "Score-recalc outbox request {OutboxId} for map {MapId}, style {Style}, track {Track} failed; it will retry.",
                        request.Id, request.MapId, request.Style, request.Track);
                }
            }
            catch (Exception updateException)
            {
                // A failed lease-state update is safe: its lease eventually expires and another scan
                // reclaims it. Never let one such error terminate the sequential worker loop.
                _logger.LogError(updateException,
                    "Failed to record score-recalc outbox failure for request {OutboxId}.", request.Id);
            }
        }
    }

    private Task<int> ReleaseCancelledScoreRecalcLeasesAsync(string leaseOwner)
    {
        string? noOwner = null;
        DateTime? noExpiry = null;
        return _db.Updateable<ScoreRecalcOutboxEntity>()
            .SetColumns(x => x.LeaseOwner == noOwner)
            .SetColumns(x => x.LeaseUntilUtc == noExpiry)
            .SetColumns(x => x.AttemptCount == (x.AttemptCount > 0 ? x.AttemptCount - 1 : 0))
            .Where(x => x.LeaseOwner == leaseOwner && x.DeadLetteredAtUtc == null)
            .ExecuteCommandAsync(OperationCancellation);
    }

    private Task<bool> IsScoreRecalcGenerationPendingAsync(ScoreRecalcOutboxEntity request)
        => _db.Queryable<ScoreRecalcOutboxEntity>()
              .Where(x => x.Id == request.Id
                          && x.RequestedGeneration == request.RequestedGeneration
                          && x.ProcessedGeneration < request.RequestedGeneration)
              .AnyAsync(OperationCancellation);

    private async Task CompleteScoreRecalcOutboxAsync(ScoreRecalcOutboxEntity request, DateTime nowUtc)
    {
        if (_db.Ado.Transaction is null)
            throw new InvalidOperationException("Outbox completion must commit with the score transaction.");

        string? noLeaseOwner = null;
        string? noError = null;
        DateTime? noDate = null;

        // The map lock excludes enqueue for this board. Lease ownership is only a scheduling
        // hint: a replacement claimant must not prevent the current generation from committing.
        var completed = await _db.Updateable<ScoreRecalcOutboxEntity>()
            .SetColumns(x => x.ProcessedGeneration == request.RequestedGeneration)
            .SetColumns(x => x.AvailableAtUtc == nowUtc)
            .SetColumns(x => x.PendingSinceUtc == noDate)
            .SetColumns(x => x.AttemptCount == 0)
            .SetColumns(x => x.LeaseOwner == noLeaseOwner)
            .SetColumns(x => x.LeaseUntilUtc == noDate)
            .SetColumns(x => x.LastError == noError)
            .SetColumns(x => x.DeadLetteredAtUtc == noDate)
            .SetColumns(x => x.UpdatedAtUtc == nowUtc)
            .Where(x => x.Id == request.Id
                        && x.RequestedGeneration == request.RequestedGeneration
                        && x.ProcessedGeneration < request.RequestedGeneration)
            .ExecuteCommandAsync(OperationCancellation);

        if (completed != 1)
            throw new InvalidOperationException("Outbox generation changed while its map was locked.");
    }

    private async Task<bool> FailScoreRecalcOutboxAsync(ScoreRecalcOutboxEntity request, string leaseOwner,
                                                         DateTime nowUtc, Exception exception)
    {
        // Claim already persisted this delivery attempt. Keep that value on failure so a hard
        // process crash and an observed handler exception share one bounded retry budget.
        var failureAttempt = Math.Max(1, request.AttemptCount);
        var deadLettered = failureAttempt >= ScoreRecalcOutboxMaxAttempts;
        var availableAtUtc = deadLettered ? nowUtc : nowUtc.Add(GetScoreRecalcBackoff(failureAttempt));
        DateTime? deadLetteredAtUtc = deadLettered ? nowUtc : null;
        string? noLeaseOwner = null;
        DateTime? noLeaseUntilUtc = null;

        // Do not stamp an older failure onto a newly enqueued generation. If one arrived, preserve
        // its reset retry state and just release this active token below.
        var failed = await _db.Updateable<ScoreRecalcOutboxEntity>()
                              .SetColumns(x => x.AvailableAtUtc == availableAtUtc)
                              .SetColumns(x => x.AttemptCount == failureAttempt)
                              .SetColumns(x => x.LeaseOwner == noLeaseOwner)
                              .SetColumns(x => x.LeaseUntilUtc == noLeaseUntilUtc)
                              .SetColumns(x => x.LastError == ToScoreRecalcErrorText(exception))
                              .SetColumns(x => x.DeadLetteredAtUtc == deadLetteredAtUtc)
                              .SetColumns(x => x.UpdatedAtUtc == nowUtc)
                              .Where(x => x.Id == request.Id
                                          && x.LeaseOwner == leaseOwner
                                          && x.RequestedGeneration == request.RequestedGeneration
                                          && x.ProcessedGeneration < request.RequestedGeneration)
                              .ExecuteCommandAsync(OperationCancellation);

        if (failed != 0)
        {
            return deadLettered;
        }

        await ReleaseLeaseForNewerGenerationAsync(request, leaseOwner, nowUtc);
        return false;
    }

    private Task<int> ReleaseLeaseForNewerGenerationAsync(ScoreRecalcOutboxEntity request, string leaseOwner,
                                                           DateTime nowUtc)
    {
        string? noLeaseOwner = null;
        DateTime? noLeaseUntilUtc = null;

        return _db.Updateable<ScoreRecalcOutboxEntity>()
                  .SetColumns(x => x.LeaseOwner == noLeaseOwner)
                  .SetColumns(x => x.LeaseUntilUtc == noLeaseUntilUtc)
                  .SetColumns(x => x.UpdatedAtUtc == nowUtc)
                  .Where(x => x.Id == request.Id
                              && x.LeaseOwner == leaseOwner
                              && x.RequestedGeneration > request.RequestedGeneration)
                  .ExecuteCommandAsync(OperationCancellation);
    }

    private static TimeSpan GetScoreRecalcBackoff(int failureAttempt)
    {
        // 1s, 2s, 4s, ... capped at 256s. Persisting AvailableAtUtc makes this survive restarts.
        var exponent = Math.Clamp(failureAttempt - 1, 0, 8);
        return TimeSpan.FromSeconds(1 << exponent);
    }

    private static string ToScoreRecalcErrorText(Exception exception)
    {
        // Persist a bounded diagnostic without duplicating stack traces or provider SQL/parameter
        // dumps into the queue table. The structured logger still receives the full exception.
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 2048 ? value : value[..2048];
    }

    private sealed class ScoreRecalcOutboxCandidate
    {
        public ulong Id { get; set; }

        public int AttemptCount { get; set; }
    }
}
