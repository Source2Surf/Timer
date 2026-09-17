using System;
using System.Threading;
using System.Threading.Tasks;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    // One token per asynchronous operation, never a mutable token shared by requests.
    // Every typed ORM call supplies this token explicitly, including CancellationToken.None
    // for legacy callers: SqlSugar otherwise retains the previous command's token.
    private readonly AsyncLocal<CancellationToken> _operationCancellation = new();
    private readonly AsyncLocal<SqlSugarClient?> _operationDb = new();

    private CancellationToken OperationCancellation
    {
        get
        {
            var token = _operationCancellation.Value;
            token.ThrowIfCancellationRequested();
            return token;
        }
    }

    internal async Task<T> RunOperationAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // CopyNew also isolates sibling tasks that inherited the same SqlSugarScope
        // execution context. Every request owns its connection, transaction and token.
        using var database = _rootDb.CopyNew();
        var previousDb = _operationDb.Value;
        _operationDb.Value = database;
        var previous = _operationCancellation.Value;
        _operationCancellation.Value = cancellationToken;
        try
        {
            return await operation();
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested
                                          && exception is not OperationCanceledException)
        {
            // Providers may wrap their cancellation exception. Preserve the transport's
            // cancellation status only after the operation has finished its rollback.
            throw new OperationCanceledException("The storage operation was cancelled.", exception, cancellationToken);
        }
        finally
        {
            _operationCancellation.Value = previous;
            _operationDb.Value = previousDb;
        }
    }

    internal Task RunOperationAsync(Func<Task> operation, CancellationToken cancellationToken)
        => RunOperationAsync(async () => { await operation(); return true; }, cancellationToken);
}
