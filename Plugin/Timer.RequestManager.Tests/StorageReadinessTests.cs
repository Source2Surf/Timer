using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Timer.RequestManager.Storage;
using SqlSugar;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class StorageReadinessTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-readiness-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public StorageReadinessTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite,
                                          $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
    }

    [Fact]
    public async Task ReadOnlyReadinessDoesNotRequireWriteTables()
    {
        _storage.Db.DbMaintenance.DropTable("surf_run_submissions");
        _storage.Db.DbMaintenance.DropTable("surf_score_recalc_outbox");

        await _storage.CheckReadyAsync();
    }

    [Fact]
    public async Task WriteReadinessReportsMissingWriteTables()
    {
        _storage.Db.DbMaintenance.DropTable("surf_run_submissions");
        _storage.Db.DbMaintenance.DropTable("surf_score_recalc_outbox");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));

        Assert.Contains("surf_run_submissions", exception.Message, StringComparison.Ordinal);
        Assert.Contains("surf_score_recalc_outbox", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteReadinessReportsMissingSubmissionIdempotencyIndex()
    {
        _storage.Db.DbMaintenance.DropIndex("idx_surf_run_submissions_submission_unique");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));

        Assert.Contains("idx_surf_run_submissions_submission_unique", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("idx_score_recalc_outbox_unique")]
    [InlineData("idx_score_recalc_outbox_pending")]
    [InlineData("idx_player_track_scores_map_style_track")]
    public async Task WriteReadinessReportsMissingRequiredWriteIndex(string indexName)
    {
        _storage.Db.DbMaintenance.DropIndex(indexName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));

        Assert.Contains(indexName, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("surf_runs_segments")]
    [InlineData("surf_maps_tracks")]
    public async Task WriteReadinessReportsMissingCoreWriteTable(string tableName)
    {
        _storage.Db.DbMaintenance.DropTable(tableName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));

        Assert.Contains(tableName, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteReadinessRejectsMissingWorkerScoreConfigurationColumn()
    {
        // The worker joins map-track tiers even for the main track. An empty or
        // partially migrated table must fail startup before runs can be accepted.
        _storage.Db.DbMaintenance.DropColumn("surf_maps_tracks", "Tier");

        await Assert.ThrowsAnyAsync<Exception>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));
    }

    [Theory]
    [InlineData(false, "SubmissionId")]
    [InlineData(true, "PayloadHash")]
    public async Task WriteReadinessRejectsAnIndexWithTheRightNameButWrongGuarantee(bool unique, string column)
    {
        const string index = "idx_surf_run_submissions_submission_unique";
        _storage.Db.DbMaintenance.DropIndex(index);
        _storage.Db.DbMaintenance.CreateIndex("surf_run_submissions", [column], index, unique);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));
        Assert.Contains(index, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteReadinessRejectsMissingJoinDateUntilMigrationCompletes()
    {
        _storage.Db.DbMaintenance.DropColumn("surf_players", "JoinedAtUtc");
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.CheckReadyAsync(requireWriteSchema: true));
        _storage.MigratePlayerJoinDates();
        await _storage.CheckReadyAsync(requireWriteSchema: true);
    }

    [Fact]
    public Task WriteReadinessAcceptsTheCompleteSchema()
        => _storage.CheckReadyAsync(requireWriteSchema: true);

    [Theory]
    [InlineData("idx_surf_maps_file")]
    [InlineData("idx_surf_players_steamid")]
    [InlineData("idx_player_best_runs_unique")]
    [InlineData("idx_player_track_scores_steam_map_style_track")]
    public async Task WriteReadinessRequiresCoreIdentityIndexes(string index)
    {
        _storage.Db.DbMaintenance.DropIndex(index);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _storage.CheckReadyAsync(requireWriteSchema: true));
        Assert.Contains(index, exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _storage.Shutdown();
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // Test cleanup should not hide the assertion result on a locked provider handle.
        }
    }
}
