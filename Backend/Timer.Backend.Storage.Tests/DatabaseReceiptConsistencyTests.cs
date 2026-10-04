using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseReceiptConsistencyTests
{
    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlReturnsTheSameReceiptTimeForAcceptanceRetryAndStatus()
        => ReceiptTimeIsStable(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlReturnsTheSameReceiptTimeForAcceptanceRetryAndStatus()
        => ReceiptTimeIsStable(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task ReceiptTimeIsStable(DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
        var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
        Assert.True(host is "127.0.0.1" or "localhost" or "::1"
            && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase),
            "Receipt acceptance requires a disposable loopback database with 'test' in its name.");

        var store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            store.Db.DbMaintenance.CreateDatabase();
            store.Init(startScoreRecalcWorker: false);
            var map = await store.GetMapInfo($"surf_receipt_{Guid.NewGuid():N}");
            var steamId = 76561198000000000L + Random.Shared.NextInt64(1, 1_000_000_000);
            await store.GetPlayerProfile(new SteamID(checked((ulong)steamId)), "Receipt acceptance");

            // Exercise several independently committed receipt timestamps. Real provider
            // DATETIME precision is part of the wire contract, which exposes milliseconds.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var command = new TimerBackendRunSubmissionCommand
                {
                    SubmissionId = Guid.NewGuid(),
                    SteamId = steamId,
                    MapName = map.MapName,
                    TimeMicros = 80_000_000 - attempt * 1_000_000,
                    FinishedAtUtc = DateTime.UtcNow,
                    RulesetVersion = 1,
                    StyleFactor = 1,
                };
                var accepted = await store.SubmitBackendRunAsync(command);
                var retry = await store.SubmitBackendRunAsync(command);
                var status = await store.GetBackendSubmissionStatusAsync(command.SubmissionId);

                Assert.NotNull(status);
                Assert.Equal(TimerBackendSubmissionDisposition.Accepted, accepted.Disposition);
                Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, retry.Disposition);
                Assert.Equal(accepted.RunId, retry.RunId);
                Assert.Equal(accepted.RunId, status.RunId);
                Assert.Equal(ToWireMilliseconds(accepted.ReceivedAtUtc), ToWireMilliseconds(retry.ReceivedAtUtc));
                Assert.Equal(ToWireMilliseconds(accepted.ReceivedAtUtc), ToWireMilliseconds(status.ReceivedAtUtc));
                Assert.Equal(1, await store.Db.Queryable<RunEntity>()
                    .Where(x => x.Id == accepted.RunId).CountAsync());
            }
        }
        finally { store.Shutdown(); }
    }

    private static long ToWireMilliseconds(DateTime value)
        => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}