using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Timer.Backend.Storage.Tests;

// Index DDL must not overlap the concurrency/worker tests using the same disposable DB.
[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseIndexReadinessTests(ITestOutputHelper output)
{
    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlRejectsMisconfiguredIndexes() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlRejectsMisconfiguredIndexes() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private async Task RunSuite(DbType type, string variable)
    {
        var connection = Environment.GetEnvironmentVariable(variable)!;
        var parsed = new DbConnectionStringBuilder { ConnectionString = connection };
        var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
        Assert.True(host is "127.0.0.1" or "localhost" or "::1"
            && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase),
            "Index acceptance requires a disposable loopback database with 'test' in its name.");

        var store = new StorageServiceImpl(type, connection, NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            store.Db.DbMaintenance.CreateDatabase();
            store.Init(startScoreRecalcWorker: false);
            await store.CheckReadyAsync(requireWriteSchema: true);
            const string inbox = "surf_run_submissions";
            const string inboxIndex = "idx_surf_run_submissions_submission_unique";

            await RejectChangedIndex(inbox, inboxIndex, ["SubmissionId"], false, ["SubmissionId"]);
            await RejectChangedIndex(inbox, inboxIndex, ["SubmissionId"], true, ["Id"]);
            await RejectChangedIndex(inbox, inboxIndex, ["SubmissionId"], true, ["SubmissionId", "Id"]);
            await RejectChangedIndex("surf_score_recalc_outbox", "idx_score_recalc_outbox_unique",
                ["MapId", "Style", "Track"], false, ["MapId", "Style", "Track"]);
            await RejectChangedIndex("surf_maps", "idx_surf_maps_file", ["File"], false, ["File"]);
            await RejectChangedIndex("surf_players", "idx_surf_players_steamid", ["SteamId"], false, ["SteamId"]);
            await RejectChangedIndex("surf_player_best_runs", "idx_player_best_runs_unique",
                ["SteamId", "MapId", "RunType", "Style", "Track", "Stage"], false,
                ["SteamId", "MapId", "RunType", "Style", "Track", "Stage"]);
            await RejectChangedIndex("surf_player_track_scores", "idx_player_track_scores_steam_map_style_track",
                ["SteamId", "MapId", "Style", "Track"], false, ["SteamId", "MapId", "Style", "Track"]);

            Assert.True(store.Db.DbMaintenance.DropIndex(inboxIndex, inbox));
            try
            {
                Assert.True(store.Db.DbMaintenance.CreateIndex("surf_runs", ["Id"], inboxIndex, true));
                await RejectReadiness(inboxIndex);
            }
            finally
            {
                store.Db.DbMaintenance.DropIndex(inboxIndex, "surf_runs");
                store.Db.DbMaintenance.CreateIndex(inbox, ["SubmissionId"], inboxIndex, true);
            }
            await store.CheckReadyAsync(requireWriteSchema: true);
            output.WriteLine($"{type}: valid schema accepted; ordinary, wrong-column, expanded-key, wrong-table indexes rejected.");

            async Task RejectChangedIndex(string table, string name, string[] correctColumns, bool unique, string[] columns)
            {
                Assert.True(store.Db.DbMaintenance.DropIndex(name, table));
                try
                {
                    Assert.True(store.Db.DbMaintenance.CreateIndex(table, columns, name, unique));
                    await RejectReadiness(name);
                    // The migration must also fail instead of treating a same-named index as repaired.
                    var migrationError = await Assert.ThrowsAsync<InvalidOperationException>(store.MigrateMasterDatabaseAsync);
                    Assert.Contains(name, migrationError.Message, StringComparison.Ordinal);
                }
                finally
                {
                    store.Db.DbMaintenance.DropIndex(name, table);
                    store.Db.DbMaintenance.CreateIndex(table, correctColumns, name, true);
                }
                await store.CheckReadyAsync(requireWriteSchema: true);
            }

            async Task RejectReadiness(string name)
            {
                var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => store.CheckReadyAsync(requireWriteSchema: true));
                Assert.Contains(name, exception.Message, StringComparison.Ordinal);
            }
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
