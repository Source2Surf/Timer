using System;
using System.Threading;
using System.Threading.Tasks;

namespace Timer.RequestManager.Backend;

public sealed partial class TimerBackendStorage
{
    private int _activeOperations;

    public Task StopWorkerAsync(CancellationToken cancellationToken)
        => _storage.StopScoreRecalcWorkerAsync(cancellationToken);

    public Task<TimerBackendWorkerHealth> GetWorkerHealthAsync(CancellationToken cancellationToken = default)
        => ExecuteAsync(_storage.GetWorkerHealthAsync, cancellationToken);

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            ThrowIfNotStarted();
            _activeOperations++;
        }
        try
        {
            return await _storage.RunOperationAsync(operation, cancellationToken);
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
