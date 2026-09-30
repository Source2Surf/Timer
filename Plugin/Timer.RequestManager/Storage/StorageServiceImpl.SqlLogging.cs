using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    internal TimeSpan SlowSqlThreshold { get; set; } = TimeSpan.FromSeconds(1);
    private const int MaxLoggedSqlLength = 4096;

    // SqlSugarScope runs this for each per-flow client it creates. SqlSugarScope.CopyNew does not:
    // it copies the source client's AOP delegates, so RunOperationAsync rebinds the slow-SQL hook.
    private void ConfigureSqlLogging(SqlSugarClient client)
    {
        client.Aop.OnError = LogSqlError;
        AttachSlowSqlHook(client);
    }

    private void AttachSlowSqlHook(SqlSugarClient client)
    {
        // Keep handlers attached by someone else, but never a hook timing another client.
        var inherited = client.CurrentConnectionConfig.AopEvents?.OnLogExecuted?.GetInvocationList()
                              .Where(handler => handler.Target is not SlowSqlHook) ?? [];
        Action<string, SugarParameter[]> hook = new SlowSqlHook(this, client).OnExecuted;
        client.Aop.OnLogExecuted = (Action<string, SugarParameter[]>?)Delegate.Combine([.. inherited, hook]);
    }

    private void LogSqlError(SqlSugarException error)
    {
        // A cancelled request is reported by its caller, not as a SQL failure.
        if (_operationCancellation.Value.IsCancellationRequested) return;

        var level = IsHandledSqlRace(error.Message) ? LogLevel.Debug : LogLevel.Warning;
        _logger.Log(level, "SQL command failed: {Error}{Sql}", error.Message,
                    FormatSqlForLog(error.Sql, error.Parametres as SugarParameter[]));
    }

    // SqlSugarException keeps only the provider's message, not the driver exception, so races the
    // callers retry or resolve (unique key, deadlock, lock timeout) are recognized by message text.
    // A miss only raises the log level.
    internal static bool IsHandledSqlRace(string message)
        => message.StartsWith("23505:", StringComparison.Ordinal)
           || message.StartsWith("40001:", StringComparison.Ordinal)
           || message.StartsWith("40P01:", StringComparison.Ordinal)
           || message.StartsWith("Duplicate entry", StringComparison.Ordinal)
           || message.StartsWith("Deadlock found", StringComparison.Ordinal)
           || message.StartsWith("Lock wait timeout exceeded", StringComparison.Ordinal);

    private static string FormatSqlForLog(string? sql, SugarParameter[]? parameters)
    {
        var text = UtilMethods.GetNativeSql(sql ?? string.Empty, parameters);
        return text.Length <= MaxLoggedSqlLength ? text : $"{text[..MaxLoggedSqlLength]}... ({text.Length} chars)";
    }

    private sealed class SlowSqlHook(StorageServiceImpl storage, SqlSugarClient client)
    {
        public void OnExecuted(string sql, SugarParameter[] parameters)
        {
            var elapsed = client.Ado.SqlExecutionTime;
            if (elapsed < storage.SlowSqlThreshold) return;

            storage._logger.LogWarning("Slow SQL took {ElapsedMs} ms:{Sql}", (long) elapsed.TotalMilliseconds,
                                       FormatSqlForLog(sql, parameters));
        }
    }
}
