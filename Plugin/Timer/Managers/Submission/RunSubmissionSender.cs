/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;

namespace Source2Surf.Timer.Managers.Submission;

/// <summary>
/// Drains the process-local <see cref="RunSubmissionSpool"/> to the v1 MagicOnion write service.
/// It is deliberately not a record manager: a submission is acknowledged only after a canonical
/// server response, and queued work has no game context from which to synthesize a local event.
/// Unacknowledged work is intentionally lost when this plugin process stops.
/// </summary>
internal sealed class RunSubmissionSender : IManager, IDisposable
{
    private readonly RunSubmissionSpool              _spool;
    private readonly BackendOptions                  _options;
    private readonly IRunSubmissionTransportFactory  _transportFactory;
    private readonly ILogger<RunSubmissionSender>    _logger;
    private readonly CancellationToken               _applicationStopping;
    private readonly object                          _lifecycleGate = new ();
    private readonly object                          _waiterGate    = new ();
    private readonly SemaphoreSlim                   _wakeSignal    = new (0, 1);
    private readonly Dictionary<Guid, List<TaskCompletionSource<SubmitRunResponse>>> _waiters = [];
    private readonly string                          _leaseOwner = $"timer-sender-{Guid.NewGuid():N}";

    private CancellationTokenSource?       _workerStopping;
    private IRunSubmissionTransport?       _transport;
    private Task?                          _worker;
    private int                            _initialized;
    private int                            _drainRequested;
    private long                           _drainClaimSequence;
    private int                            _shutdownState;
    private int                            _resourcesDisposed;

    public RunSubmissionSender(RunSubmissionSpool             spool,
                               BackendOptions                 options,
                               IRunSubmissionTransportFactory transportFactory,
                               InterfaceBridge                bridge,
                               ILogger<RunSubmissionSender>   logger)
        : this(spool, options, transportFactory, GetApplicationStoppingToken(bridge), logger)
    {
    }

    internal RunSubmissionSender(RunSubmissionSpool             spool,
                                 BackendOptions                 options,
                                 IRunSubmissionTransportFactory transportFactory,
                                 CancellationToken              applicationStopping,
                                 ILogger<RunSubmissionSender>   logger)
    {
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _spool               = spool;
        _options             = options;
        _transportFactory    = transportFactory;
        _applicationStopping = applicationStopping;
        _logger              = logger;
    }

    public bool Init()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _initialized) != 0)
            {
                return true;
            }

            try
            {
                if (_spool.TryGetLegacyDatabasePath(out var legacyDatabasePath))
                {
                    _logger.LogCritical(
                        "Legacy run submission database exists at {LegacyDatabasePath}; the remote sender will not start. "
                        + "Use a prior plugin version to drain it, or manually export/archive it after verifying its contents. "
                        + "The file was not opened, moved, or deleted.",
                        legacyDatabasePath);
                    return false;
                }

                if (!_spool.Init())
                {
                    return false;
                }

                _transport      = _transportFactory.Create();
                _workerStopping = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
                Volatile.Write(ref _initialized, 1);

                // Calling the async loop directly avoids dedicating a ThreadPool thread merely
                // to wait on asynchronous network I/O.
                _worker = SendLoopAsync(_workerStopping.Token);

                _logger.LogInformation("Run submission sender initialized for {Endpoint}.", _options.Endpoint);
                return true;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to initialize in-memory run submission sender.");
                DisposeResourcesNoThrow();
                return false;
            }
        }
    }

    /// <summary>
    /// Enqueues the v1 request in memory before waiting. The returned task completes only after
    /// the exact submission id receives an Accepted or AlreadyApplied response and that queued
    /// entry is removed. Cancelling the waiter never deletes or mutates the queued submission.
    /// </summary>
    internal async Task<SubmitRunResponse> EnqueueAndWaitAsync(SubmitRunRequest request,
                                                                CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAcceptingSubmissions();

        TaskCompletionSource<SubmitRunResponse> waiter;
        Guid                                     submissionId;

        // Completion also locks this gate. Holding it across enqueue+registration closes the
        // race where an exceptionally fast RPC acknowledgement could otherwise delete the row
        // before the local caller starts waiting.
        lock (_waiterGate)
        {
            var enqueue = _spool.Enqueue(request);
            submissionId = enqueue.SubmissionId;

            switch (enqueue.Disposition)
            {
                case SubmissionSpoolEnqueueDisposition.Enqueued:
                case SubmissionSpoolEnqueueDisposition.AlreadyQueued:
                    break;
                case SubmissionSpoolEnqueueDisposition.ConflictingSubmissionId:
                case SubmissionSpoolEnqueueDisposition.CapacityExceeded:
                    throw new RunSubmissionEnqueueException(enqueue.SubmissionId, enqueue.Disposition);
                default:
                    throw new InvalidOperationException($"Unexpected submission spool result '{enqueue.Disposition}'.");
            }

            waiter = new TaskCompletionSource<SubmitRunResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(submissionId, out var pending))
            {
                pending = [];
                _waiters.Add(submissionId, pending);
            }

            pending.Add(waiter);
        }

        SignalWorker();

        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RemoveWaiter(submissionId, waiter);
        }
    }

    /// <summary>
    /// Uses the sender's existing MagicOnion transport to ensure a player profile.
    /// This is a direct, caller-cancellable RPC rather than a queued operation; it shares the
    /// configured deadline and channel with run submissions.
    /// </summary>
    internal Task<EnsurePlayerProfileResponse> EnsurePlayerProfileAsync(long              steamId,
                                                                          string            name,
                                                                          CancellationToken cancellationToken = default)
    {
        if (steamId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(steamId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        EnsureAcceptingSubmissions();

        return CallWithDeadlineAsync(
            token => Transport.EnsurePlayerProfileAsync(
                new EnsurePlayerProfileRequest { SteamId = steamId, Name = name }, token),
            cancellationToken);
    }

    /// <summary>
    /// Requests a bounded drain of entries that are due now. If that drain exceeds the configured
    /// deadline, in-flight calls are cancelled and their leases are released while the process
    /// is still alive. Closing the sender then discards any unacknowledged in-memory work.
    /// </summary>
    public void Shutdown()
    {
        Task?                    worker;
        CancellationTokenSource? workerStopping;

        lock (_lifecycleGate)
        {
            if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _drainClaimSequence, _spool.CurrentClaimSequence);
            Volatile.Write(ref _drainRequested, 1);
            worker         = _worker;
            workerStopping = _workerStopping;
        }

        CancelOutstandingWaiters();
        SignalWorker();

        if (worker is not null && !WaitForWorker(worker, _options.ShutdownDrainTimeout))
        {
            _logger.LogWarning("Run submission sender drain exceeded {DrainTimeout}; cancelling in-flight RPCs.",
                               _options.ShutdownDrainTimeout);
            workerStopping?.Cancel();

            // A MagicOnion/gRPC call observes this token. Do not dispose the spool/channel while
            // a non-cooperative test or third-party transport can still use it.
            if (!WaitForWorker(worker, TimeSpan.FromSeconds(2)))
            {
                _logger.LogError("Run submission sender did not stop after cancellation; retaining transport resources until process exit.");
                return;
            }
        }

        if (Volatile.Read(ref _initialized) != 0)
        {
            try
            {
                var remaining = _spool.GetSnapshot();
                if (remaining.TotalCount > 0)
                {
                    _logger.LogWarning(
                        "Run submission sender is shutting down with {PendingCount} unconfirmed and {QuarantinedCount} quarantined in-memory entries. They cannot be recovered after process exit.",
                        remaining.PendingCount, remaining.QuarantinedCount);
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Could not count unconfirmed run submissions before shutdown.");
            }
        }

        DisposeResourcesNoThrow();
    }

    public void Dispose()
    {
        Shutdown();
        GC.SuppressFinalize(this);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Read the drain flag once per iteration: the claim and the exit decision must
                // agree, or a drain requested mid-iteration would exit before its final attempts.
                var draining = Volatile.Read(ref _drainRequested) != 0;
                bool processed;
                try
                {
                    processed = await DispatchDueBatchAsync(draining, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // A spool read failure is not an acknowledgement and should not bring down
                    // the timer. Sleep/wake before retrying, retaining the in-memory entries.
                    _logger.LogError(exception, "Run submission sender could not claim its next batch.");
                    processed = false;
                }

                if (draining && !processed)
                {
                    break;
                }

                if (processed)
                {
                    continue;
                }

                try
                {
                    await _wakeSignal.WaitAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            // Claims not currently being processed are released by DispatchDueBatchAsync.
            // Shutdown subsequently clears the volatile queue by design.
        }
    }

    private async Task<bool> DispatchDueBatchAsync(bool draining, CancellationToken cancellationToken)
    {
        // While draining for shutdown, also give entries waiting out a retry backoff one last
        // attempt: the backend may have recovered, and the volatile queue is discarded next.
        long? finalAttemptForClaimsUpTo = draining ? Interlocked.Read(ref _drainClaimSequence) : null;
        var leases = _spool.ClaimDueBatch(_options.BatchSize,
                                           _leaseOwner,
                                           retryIndefinitely: true,
                                           finalAttemptForClaimsUpTo: finalAttemptForClaimsUpTo);

        if (leases.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < leases.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                ReleaseRemainingLeases(leases, index);
                return true;
            }

            await DeliverLeaseAsync(leases[index], cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    private async Task DeliverLeaseAsync(RunSubmissionSpoolLease lease, CancellationToken cancellationToken)
    {
        try
        {
            var response = await CallWithDeadlineAsync(token => Transport.SubmitRunAsync(lease.Request, token),
                                                        cancellationToken)
                                .ConfigureAwait(false);

            if (IsCanonical(response, lease.SubmissionId))
            {
                ConfirmAcknowledgement(lease, response);
                return;
            }

            ResolveAmbiguousOutcome(lease, "SubmitRun returned a non-canonical response.", cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseLeaseForShutdown(lease);
        }
        catch (Exception exception) when (IsPermanent(exception))
        {
            QuarantineLease(lease, DescribePermanentFailure(exception));
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound
                                            && IsOlderThan(lease, NotFoundRetryWindow))
        {
            // A map or player still missing hours after the finish will not be provisioned by
            // retrying. Stop occupying a queue slot with a request that can never succeed.
            QuarantineLease(lease, $"Backend still reports NotFound more than {NotFoundRetryWindow.TotalHours:0} hours after the run finished.");
        }
        catch (Exception exception)
        {
            ResolveAmbiguousOutcome(lease, DescribeRetryableFailure("SubmitRun", exception), cancellationToken);
        }
    }

    private static bool IsOlderThan(RunSubmissionSpoolLease lease, TimeSpan age)
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - lease.Request.FinishedAtUnixTimeMilliseconds
           > (long)age.TotalMilliseconds;

    private void ResolveAmbiguousOutcome(RunSubmissionSpoolLease lease,
                                         string                  reason,
                                         CancellationToken       cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            ReleaseLeaseForShutdown(lease);
            return;
        }

        // Status is keyed only by SubmissionId and cannot prove that a stored payload is
        // this lease's payload. Retrying the original SubmitRun is the safe authority:
        // the backend compares the canonical payload hash and returns AlreadyApplied or
        // a permanent conflict. This also avoids a second RPC on every transport error.
        RetryLease(lease, reason);
    }

    private async Task<TResponse> CallWithDeadlineAsync<TResponse>(Func<CancellationToken, Task<TResponse>> operation,
                                                                    CancellationToken                       cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RpcDeadline);
        return await operation(deadline.Token).ConfigureAwait(false);
    }

    private void ConfirmAcknowledgement(RunSubmissionSpoolLease lease, SubmitRunResponse response)
    {
        if (!_spool.Acknowledge(lease.SubmissionId, lease.LeaseToken))
        {
            _logger.LogWarning("Could not acknowledge run submission {SubmissionId}; its lease is no longer current.",
                               lease.SubmissionId);
            return;
        }

        CompleteAcknowledgement(lease.SubmissionId, response);
        _logger.LogDebug("Acknowledged in-memory run submission {SubmissionId} ({Disposition}).",
                         lease.SubmissionId,
                         response.Disposition);
    }

    private void RetryLease(RunSubmissionSpoolLease lease, string reason)
    {
        if (!_spool.Retry(lease.SubmissionId, lease.LeaseToken, reason))
        {
            _logger.LogWarning("Could not schedule retry for run submission {SubmissionId}; its lease is no longer current.",
                               lease.SubmissionId);
            return;
        }

        _logger.LogWarning("Will retry in-memory run submission {SubmissionId}: {Reason}", lease.SubmissionId, reason);
    }

    private void QuarantineLease(RunSubmissionSpoolLease lease, string reason)
    {
        if (!_spool.Quarantine(lease.SubmissionId, lease.LeaseToken, reason))
        {
            _logger.LogWarning("Could not quarantine run submission {SubmissionId}; its lease is no longer current.",
                               lease.SubmissionId);
            return;
        }

        CompleteRejection(lease.SubmissionId, reason);
        _logger.LogError("Quarantined in-memory run submission {SubmissionId}: {Reason}", lease.SubmissionId, reason);
    }

    private void ReleaseLeaseForShutdown(RunSubmissionSpoolLease lease)
    {
        if (_spool.ReleaseLease(lease.SubmissionId, lease.LeaseToken))
        {
            _logger.LogDebug("Released in-flight run submission {SubmissionId} during shutdown.", lease.SubmissionId);
        }
    }

    private void ReleaseRemainingLeases(IReadOnlyList<RunSubmissionSpoolLease> leases, int startIndex)
    {
        for (var index = startIndex; index < leases.Count; index++)
        {
            ReleaseLeaseForShutdown(leases[index]);
        }
    }

    private static bool IsCanonical(SubmitRunResponse? response, Guid submissionId)
    {
        return response is not null
               && response.SubmissionId == submissionId
               && response.RunId != 0
               && response.RunId <= long.MaxValue
               && response.ReceivedAtUnixTimeMilliseconds > 0
               && response.Disposition is SubmissionDisposition.Accepted or SubmissionDisposition.AlreadyApplied
               && (response.AttemptResult is AttemptResult.NoNewRecord
                   or AttemptResult.NewPersonalRecord
                   or AttemptResult.NewServerRecord)
               && (response.RankState is RankState.NotApplicable or RankState.Pending or RankState.Ready)
               && response.Rank >= 0;
    }

    private static bool IsPermanent(Exception exception)
    {
        // NotFound is intentionally absent: player-profile or map provisioning can race a run
        // submission, so it receives the same idempotent retry path as an
        // unavailable backend rather than being silently dead-lettered.
        // A 404 synthesized from a non-gRPC HTTP response (e.g. a proxy while the backend is
        // being redeployed) did not come from the backend and is retried. A proxy's 401/403
        // usually means an auth misconfiguration that retrying cannot fix, so it stays permanent.
        return exception is RpcException { StatusCode: StatusCode.Unauthenticated
                                           or StatusCode.PermissionDenied
                                           or StatusCode.InvalidArgument
                                           or StatusCode.FailedPrecondition
                                           or StatusCode.AlreadyExists
                                           or StatusCode.OutOfRange
                                           or StatusCode.Unimplemented } rpcException
               && !IsHttpIntermediaryNotFound(rpcException);
    }

    /// <summary>
    /// True when Grpc.Net.Client synthesized Unimplemented from a plain HTTP 404 that carried no
    /// grpc-status, i.e. something in front of the backend (such as a proxy mid-redeploy)
    /// answered, not the backend's own gRPC service.
    /// </summary>
    internal static bool IsHttpIntermediaryNotFound(RpcException exception)
        => exception.StatusCode == StatusCode.Unimplemented
           && exception.Status.Detail?.StartsWith(HttpIntermediaryStatusDetailPrefix, StringComparison.Ordinal) == true;

    private const string HttpIntermediaryStatusDetailPrefix = "Bad gRPC response. HTTP status code: ";

    private static readonly TimeSpan NotFoundRetryWindow = TimeSpan.FromHours(6);

    private static string DescribePermanentFailure(Exception exception)
    {
        return exception is RpcException rpcException
            ? $"Permanent gRPC status: {rpcException.StatusCode}."
            : "Permanent run submission failure.";
    }

    private static string DescribeRetryableFailure(string operation, Exception exception)
    {
        return exception is RpcException rpcException
            ? $"{operation} gRPC status: {rpcException.StatusCode}."
            : $"{operation} transport failure: {exception.GetType().Name}.";
    }

    private void EnsureAcceptingSubmissions()
    {
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _initialized) == 0 || _worker is null || _worker.IsCompleted)
            {
                throw new InvalidOperationException("The in-memory run submission sender is not initialized.");
            }

            if (Volatile.Read(ref _shutdownState) != 0)
            {
                throw new ObjectDisposedException(nameof(RunSubmissionSender));
            }
        }
    }

    private void SignalWorker()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A producer already signaled the one-slot wake semaphore.
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already joined the worker and released resources.
        }
    }

    private void CompleteAcknowledgement(Guid submissionId, SubmitRunResponse response)
    {
        foreach (var waiter in TakeWaiters(submissionId))
        {
            waiter.TrySetResult(response);
        }
    }

    private void CompleteRejection(Guid submissionId, string reason)
    {
        foreach (var waiter in TakeWaiters(submissionId))
        {
            waiter.TrySetException(new RunSubmissionRejectedException(submissionId, reason));
        }
    }

    private List<TaskCompletionSource<SubmitRunResponse>> TakeWaiters(Guid submissionId)
    {
        lock (_waiterGate)
        {
            if (!_waiters.Remove(submissionId, out var waiters))
            {
                return [];
            }

            return waiters;
        }
    }

    private void RemoveWaiter(Guid submissionId, TaskCompletionSource<SubmitRunResponse> waiter)
    {
        lock (_waiterGate)
        {
            if (!_waiters.TryGetValue(submissionId, out var waiters))
            {
                return;
            }

            waiters.Remove(waiter);
            if (waiters.Count == 0)
            {
                _waiters.Remove(submissionId);
            }
        }
    }

    private void CancelOutstandingWaiters()
    {
        List<TaskCompletionSource<SubmitRunResponse>> waiters;
        lock (_waiterGate)
        {
            waiters = [];
            foreach (var pending in _waiters.Values)
            {
                waiters.AddRange(pending);
            }

            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled();
        }
    }

    private bool WaitForWorker(Task worker, TimeSpan timeout)
    {
        try
        {
            return worker.Wait(timeout);
        }
        catch (AggregateException exception)
        {
            _logger.LogError(exception, "Run submission sender worker terminated with an unhandled error.");
            return true;
        }
    }

    private void DisposeResourcesNoThrow()
    {
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            _transport?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the run submission transport.");
        }

        try
        {
            _workerStopping?.Dispose();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to dispose the run submission sender cancellation source.");
        }

        try
        {
            _spool.Shutdown();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to close the in-memory run submission queue.");
        }

        _wakeSignal.Dispose();
    }

    private IRunSubmissionTransport Transport
        => _transport ?? throw new InvalidOperationException("The run submission transport is not initialized.");

    private static CancellationToken GetApplicationStoppingToken(InterfaceBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        return bridge.CancellationToken;
    }
}

/// <summary>Raised before a queued request can be observed by the in-memory sender.</summary>
internal sealed class RunSubmissionEnqueueException : InvalidOperationException
{
    public RunSubmissionEnqueueException(Guid submissionId, SubmissionSpoolEnqueueDisposition disposition)
        : base($"Run submission {submissionId} could not be queued: {disposition}.")
    {
        SubmissionId = submissionId;
        Disposition  = disposition;
    }

    public Guid SubmissionId { get; }

    public SubmissionSpoolEnqueueDisposition Disposition { get; }
}

/// <summary>
/// Raised to a live waiter only after the underlying entry has been quarantined in memory. It is
/// never used for an ambiguous transport failure, because that entry remains queued to retry.
/// </summary>
internal sealed class RunSubmissionRejectedException : InvalidOperationException
{
    public RunSubmissionRejectedException(Guid submissionId, string reason)
        : base($"Run submission {submissionId} was quarantined: {reason}")
    {
        SubmissionId = submissionId;
    }

    public Guid SubmissionId { get; }
}
