using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    // Every record mutation takes the map primary-key lock first, followed by
    // player locks for points. Ordinary record reads stay unlocked. SqlSugar's
    // TranLock keeps the complete transaction portable across MySQL and PG.
    private Task BeginRecordTransactionAsync()
        => _db.Ado.BeginTranAsync(_db.CurrentConnectionConfig.DbType == SqlSugar.DbType.Sqlite
                                     ? IsolationLevel.Serializable
                                     : IsolationLevel.ReadCommitted);

    private async Task LockMapAsync(ulong mapId)
    {
        if (_db.Ado.Transaction is null)
            throw new InvalidOperationException("Map locks require an active transaction.");
        var query = _db.Queryable<MapEntity>().Where(x => x.MapId == mapId);
        if (_db.CurrentConnectionConfig.DbType != SqlSugar.DbType.Sqlite) query = query.TranLock(DbLockType.Wait);
        if (await query.FirstAsync() is null) throw new InvalidOperationException($"Map {mapId} does not exist.");
    }

    private async Task WithRecordTransactionAsync(Func<Task> action)
    {
        // Retry only failures which guarantee that this transaction did not commit.
        // Never retry a connection loss at COMMIT: its outcome can be ambiguous.
        for (var attempt = 0; ; attempt++)
        {
            await BeginRecordTransactionAsync();
            try
            {
                await action();
                await _db.Ado.CommitTranAsync();
                return;
            }
            catch (Exception ex)
            {
                var rolledBack = true;
                try { await _db.Ado.RollbackTranAsync(); }
                catch (Exception rollbackError)
                {
                    rolledBack = false;
                    _logger.LogError(rollbackError, "Failed to roll back record transaction.");
                }
                if (!rolledBack || attempt >= 2 || !IsTransactionConflict(ex)) throw;
                _logger.LogWarning(ex, "Retrying record transaction after a lock conflict (attempt {Attempt}).", attempt + 1);
                await Task.Delay(Random.Shared.Next(20, 60) * (attempt + 1));
            }
        }
    }

    private static bool IsTransactionConflict(Exception ex)
        => ex is MySqlConnector.MySqlException { Number: 1213 or 1205 }
               or Npgsql.PostgresException { SqlState: "40001" or "40P01" }
           || (ex.InnerException is { } inner && IsTransactionConflict(inner));

    internal async Task<bool> SaveReplayMetadataAsync(ReplayEntity replay)
    {
        var saved = false;
        await WithRecordTransactionAsync(async () =>
        {
            saved = false;
            await LockMapAsync(replay.MapId);
            var run = _db.Queryable<RunEntity>().Where(x => x.Id == replay.RunId && x.MapId == replay.MapId && x.SteamId == replay.SteamId);
            if (await run.FirstAsync() is null) return;
            // The map lock serializes duplicate uploads and record removal.
            // External replay storage is accessed outside this transaction.
            await _db.Storageable(replay).ExecuteCommandAsync();
            saved = true;
        });
        return saved;
    }
}
