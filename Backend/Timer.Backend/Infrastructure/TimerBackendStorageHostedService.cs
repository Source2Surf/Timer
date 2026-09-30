using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Timer.Backend.Configuration;
using Timer.RequestManager.Backend;

namespace Timer.Backend.Infrastructure;

/// <summary>
/// Starts storage with the ASP.NET host. The DI container owns disposal so it happens
/// after the web server has stopped and drained request services.
/// </summary>
internal sealed class TimerBackendStorageHostedService : IHostedService
{
    private readonly TimerBackendStorage _storage;
    private readonly TimerBackendOptions _options;
    private readonly TimerWriteApiOptions _writeApiOptions;
    private readonly ILogger<TimerBackendStorageHostedService> _logger;
    private readonly TimerBackendRuntimeOptions _runtimeOptions;

    public TimerBackendStorageHostedService(TimerBackendStorage                       storage,
                                             TimerBackendOptions                       options,
                                             TimerWriteApiOptions                       writeApiOptions,
                                             ILogger<TimerBackendStorageHostedService> logger,
                                             TimerBackendRuntimeOptions? runtimeOptions = null)
    {
        _storage = storage;
        _options = options;
        _writeApiOptions = writeApiOptions;
        _logger  = logger;
        _runtimeOptions = runtimeOptions ?? new TimerBackendRuntimeOptions();
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.InitializeSchema || _options.AllowReadRepair
            || _options.EnableOutboxWorker || _writeApiOptions.Enabled)
        {
            _logger.LogWarning(
                "Timer backend is starting with database mutations enabled (initializeSchema={InitializeSchema}, allowReadRepair={AllowReadRepair}, enableOutboxWorker={EnableOutboxWorker}, writeApi={WriteApi}). Do not use a read-only replica role for this instance.",
                _options.InitializeSchema,
                _options.AllowReadRepair,
                _options.EnableOutboxWorker,
                _writeApiOptions.Enabled);
        }

        _storage.Start(_options.InitializeSchema,
                       _options.AllowReadRepair,
                       requireWriteSchema: _options.ShouldRunOutboxWorker(_writeApiOptions.Enabled));
        _logger.LogInformation(
            "Timer SQL storage started (initializeSchema={InitializeSchema}, allowReadRepair={AllowReadRepair}, enableOutboxWorker={EnableOutboxWorker}, writeApi={WriteApi}).",
            _options.InitializeSchema,
            _options.AllowReadRepair,
            _options.EnableOutboxWorker,
            _writeApiOptions.Enabled);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_runtimeOptions.WorkerShutdownTimeout);
        try { await _storage.StopWorkerAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _logger.LogWarning("Score worker exceeded the shutdown grace period; its SQL scope will be released after its active operation finishes.");
        }
        // Request storage stays alive until Kestrel drains and DI is disposed. A late
        // request also retains its scope until rollback/commit has actually finished.
    }
}
