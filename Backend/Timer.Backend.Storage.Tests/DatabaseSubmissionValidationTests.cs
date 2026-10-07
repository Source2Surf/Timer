using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseSubmissionValidationTests
{
    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlRejectsUnpersistableCompletionTime() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlUsesTheSameCompletionTimeBounds() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task RunSuite(DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
        var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
        Assert.True(host is "127.0.0.1" or "localhost" or "::1"
            && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase),
            "Submission acceptance requires a disposable loopback database with 'test' in its name.");

        var store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            store.Init(startScoreRecalcWorker: false);
            var map = await store.GetMapInfo($"surf_timestamp_{Guid.NewGuid():N}");
            var steamId = 76561198000000000L + Random.Shared.NextInt64(1, 1_000_000_000);
            await store.GetPlayerProfile(new SteamID(checked((ulong)steamId)), "Timestamp acceptance");
            var tooLate = new DateTime(9999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc);
            var tooEarly = new DateTime(999, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc);
            foreach (var finishedAt in new[] { tooLate, tooEarly })
            {
                var command = CreateCommand(finishedAt);
                await Assert.ThrowsAsync<TimerBackendSubmissionValidationException>(
                    () => store.SubmitBackendRunAsync(command));
                Assert.Null(await store.GetBackendSubmissionStatusAsync(command.SubmissionId));
            }
            Assert.False(await store.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).AnyAsync());

            // Both ends of the shared DATETIME range must remain usable with checkpoints.
            foreach (var finishedAt in new[]
                {
                    new DateTime(1000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                })
            {
                var command = CreateCommand(finishedAt);
                var accepted = await store.SubmitBackendRunAsync(command);
                var replayed = await store.SubmitBackendRunAsync(command);
                Assert.Equal(TimerBackendSubmissionDisposition.Accepted, accepted.Disposition);
                Assert.Equal(accepted.RunId, replayed.RunId);
                Assert.Equal(TimerBackendSubmissionDisposition.AlreadyApplied, replayed.Disposition);
                var segment = await store.Db.Queryable<RunSegmentEntity>()
                    .Where(x => x.RunId == accepted.RunId).SingleAsync();
                Assert.Equal(StorageServiceImpl.ToUnixTimeMilliseconds(finishedAt), segment.DateUnixMilliseconds);
            }

            TimerBackendRunSubmissionCommand CreateCommand(DateTime finishedAt) => new()
            {
                SubmissionId = Guid.NewGuid(), SteamId = steamId, MapName = map.MapName,
                TimeMicros = 80_000_000, FinishedAtUtc = finishedAt, RulesetVersion = 1,
                StyleFactor = 1, Checkpoints =
                [
                    new TimerBackendSubmissionCheckpoint { CheckpointIndex = 1, TimeMicros = 40_000_000 },
                ],
            };
        }
        finally { store.Shutdown(); }
    }

    private sealed class DisposableDatabaseFactAttribute : FactAttribute
    {
        public DisposableDatabaseFactAttribute(string variable)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
                Skip = $"Set {variable} to a disposable loopback test database.";
        }
    }
}
