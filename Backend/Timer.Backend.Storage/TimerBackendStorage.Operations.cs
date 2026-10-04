using System;
using System.Threading;
using System.Threading.Tasks;

namespace Timer.Backend.Storage;

public sealed partial class TimerBackendStorage
{
    private int _activeOperations;

    public Task StopWorkerAsync(CancellationToken cancellationToken)
        => _storage.StopScoreRecalcWorkerAsync(cancellationToken);

    public Task<TimerBackendWorkerHealth> GetWorkerHealthAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(_storage.GetWorkerHealthAsync, cancellationToken);

    private static readonly TimeSpan ReachabilityProbeTimeout = TimeSpan.FromSeconds(3);
    private const long ReachabilityCacheTicks = 2 * TimeSpan.TicksPerSecond;
    private long _lastReachabilityProbeTicks;
    private int _lastReachabilityProbeSucceeded;

    /// <summary>
    /// Decides whether a failed operation means the database itself is unavailable (reported as
    /// 503 / Unavailable) rather than a server-side bug (500 / Internal). Driver exceptions that
    /// say so are classified directly, but SqlSugar wraps connection failures in a plain
    /// SqlSugarException carrying only the driver's message text, so otherwise a trivial query
    /// decides: if the database cannot answer it either, it is unavailable. The probe runs only on
    /// the error path and its result is reused briefly so an outage does not double connection
    /// attempts.
    /// </summary>
    private async Task<bool> IsDatabaseUnavailableAsync(Exception exception, CancellationToken cancellationToken)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is MySqlConnector.MySqlException { IsTransient: true }
                    or Npgsql.NpgsqlException { IsTransient: true }
                    or System.Net.Sockets.SocketException
                    or TimeoutException)
            {
                return true;
            }
        }

        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastReachabilityProbeTicks) < ReachabilityCacheTicks)
        {
            return Volatile.Read(ref _lastReachabilityProbeSucceeded) == 0;
        }

        bool reachable;
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            probe.CancelAfter(ReachabilityProbeTimeout);
            try
            {
                await _storage.RunOperationAsync(_storage.PingAsync, probe.Token);
                reachable = true;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Includes the probe's own timeout; a caller cancellation propagates instead.
                reachable = false;
            }
        }

        Volatile.Write(ref _lastReachabilityProbeSucceeded, reachable ? 1 : 0);
        Interlocked.Exchange(ref _lastReachabilityProbeTicks, now);
        return !reachable;
    }

    internal async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ThrowIfNotStarted();
            _activeOperations++;
        }
        try
        {
            // Request tokens carry the request deadline; the CLI's default token has none.
            return await _storage.RunOperationAsync(operation, cancellationToken,
                                                    deadlineInToken: cancellationToken.CanBeCanceled);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                              and not TimerBackendSubmissionException
                                              and not TimerBackendUnavailableException)
        {
            if (await IsDatabaseUnavailableAsync(exception, cancellationToken))
            {
                throw new TimerBackendUnavailableException(exception);
            }

            throw;
        }
        finally
        {
            lock (_lifecycleLock)
            {
                _activeOperations--;
                // Host shutdown may exhaust its grace period while a provider is still
                // completing rollback. The last request owns deferred scope disposal.
                if (_disposed != 0 && _activeOperations == 0) _storage.Shutdown();
            }
        }
    }
}
