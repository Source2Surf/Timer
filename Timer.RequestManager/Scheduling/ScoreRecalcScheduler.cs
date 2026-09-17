using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Timer.RequestManager.Scheduling;

/// <summary>
/// Single-consumer wake loop for the durable score-recalculation outbox.
///
/// The channel carries only a wake signal: the database is the authoritative queue. A periodic
/// scan makes a dropped/coalesced wake harmless, and one consumer processes each leased batch
/// sequentially.
/// </summary>
internal sealed class ScoreRecalcScheduler : IDisposable
{
    // Enqueue persists the five-second coalescing deadline in AvailableAtUtc. Poll every second so
    // an early wake consumed before that deadline cannot make an eligible row wait a long time.
    private static readonly TimeSpan DefaultScanInterval = TimeSpan.FromSeconds(1);

    private readonly Channel<byte> _wakeChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<CancellationToken, Task<int>> _processBatchAsync;
    private readonly TimeSpan _scanInterval;
    private readonly ILogger? _logger;
    private readonly object _lifecycleGate = new();

    private Task? _consumer;
    private Task _cancellation = Task.CompletedTask;
    private bool _disposed;
    private bool _stopping;
    private long _lastScanUtcTicks;

    public Task Completion { get { lock (_lifecycleGate) return Task.WhenAll(_consumer ?? Task.CompletedTask, _cancellation); } }
    public DateTime? LastSuccessfulScanUtc
        => Interlocked.Read(ref _lastScanUtcTicks) is var ticks && ticks != 0
            ? new DateTime(ticks, DateTimeKind.Utc) : null;

    public ScoreRecalcScheduler(Func<Task<int>> processBatchAsync, ILogger? logger = null,
                                TimeSpan? scanInterval = null)
        : this(_ => processBatchAsync(), logger, scanInterval)
    {
        ArgumentNullException.ThrowIfNull(processBatchAsync);
    }

    public ScoreRecalcScheduler(Func<CancellationToken, Task<int>> processBatchAsync, ILogger? logger = null,
                                TimeSpan? scanInterval = null)
    {
        _processBatchAsync = processBatchAsync ?? throw new ArgumentNullException(nameof(processBatchAsync));
        _logger = logger;
        _scanInterval = scanInterval ?? DefaultScanInterval;
        if (_scanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scanInterval), "Scan interval must be positive.");
        }
    }

    /// <summary>
    /// Starts the initial scan and subsequent periodic polling. Construction is intentionally inert:
    /// callers must create schema/migrations before starting a worker.
    /// </summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _stopping || _consumer is not null)
            {
                return;
            }

            _consumer = Task.Run(ConsumeLoopAsync);
        }
    }

    /// <summary>
    /// Signals that a committed outbox write may be available. It deliberately contains no work
    /// payload; the consumer always reclaims work from durable storage.
    /// </summary>
    public void Wake()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _wakeChannel.Writer.TryWrite(0);
        }
    }

    private async Task ConsumeLoopAsync()
    {
        var ct = _cts.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Keep draining immediately while there is durable work. The callback is awaited,
                // so one scheduler instance never processes two score recalculations concurrently.
                var processed = await _processBatchAsync(ct);
                Interlocked.Exchange(ref _lastScanUtcTicks, DateTime.UtcNow.Ticks);
                if (processed > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Unexpected error while scanning the score-recalc outbox.");
            }

            try
            {
                await WaitForWakeOrScanAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task WaitForWakeOrScanAsync(CancellationToken ct)
    {
        // Do not use Task.WhenAny with an uncancelled WaitToReadAsync: when the timer wins, that
        // pending reader remains registered and can consume a later wake. A linked per-scan token
        // cancels the one outstanding channel wait on timeout.
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(_scanInterval);
        try
        {
            await _wakeChannel.Reader.WaitToReadAsync(scanCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Periodic scan timeout; fall through to the next durable database read.
        }

        DrainWakeSignals();
    }

    private void DrainWakeSignals()
    {
        while (_wakeChannel.Reader.TryRead(out _))
        {
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestStop();
        await Completion.WaitAsync(cancellationToken);
    }

    public void RequestStop()
    {
        lock (_lifecycleGate)
        {
            if (_stopping) return;
            _stopping = true;
            _wakeChannel.Writer.TryComplete();
            // Provider cancellation callbacks may perform network work. Dispatch them
            // asynchronously so StopAsync can still honor its own shutdown deadline.
            _cancellation = _cts.CancelAsync();
        }
    }

    public void Dispose()
    {
        RequestStop();
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // StopAsync owns the caller's bounded wait. Resources stay alive for a callback
        // that ignores cancellation; never dispose its token/source underneath it.
        _ = DisposeAfterCompletionAsync();
    }

    private async Task DisposeAfterCompletionAsync()
    {
        try { await Completion; }
        catch (Exception ex) { _logger?.LogError(ex, "Score-recalc worker stopped with an unexpected error."); }
        finally { _cts.Dispose(); }
    }
}
