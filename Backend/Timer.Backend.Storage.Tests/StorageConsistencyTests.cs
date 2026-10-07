using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class StorageConsistencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-consistency-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;
    private static readonly SteamID Player = new(76561198000000001);

    public StorageConsistencyTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Db.CodeFirst.InitTables(typeof(MapEntity), typeof(MapTrackEntity), typeof(PlayerEntity), typeof(RunEntity),
                                        typeof(PlayerBestRunEntity), typeof(PlayerTrackScoreEntity), typeof(PlayerMapStatsEntity), typeof(ReplayEntity),
                                        typeof(ScoreRecalcOutboxEntity));
    }

    [Fact]
    public async Task CompletionFailureRollsBackScoresAndPlayerTotalsTogether()
    {
        var map = await _storage.GetMapInfo("surf_completion_rollback");
        await _storage.GetPlayerProfile(Player, "Player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, Time = 80,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).ExecuteCommandAsync();
        await _storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var originalPoints = (await _storage.Db.Queryable<PlayerEntity>().SingleAsync()).Points;
        var now = DateTime.UtcNow;
        await _storage.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 2, now);
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
                || !sql.Contains("surf_score_recalc_outbox", StringComparison.OrdinalIgnoreCase)) return;
            var where = sql.IndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            var processed = sql.IndexOf("ProcessedGeneration", StringComparison.OrdinalIgnoreCase);
            if (processed >= 0 && processed < where) throw new InvalidOperationException("Injected completion failure");
        };
        try { await _storage.ProcessScoreRecalcOutboxBatchAsync(now.AddSeconds(5), "completion-failure"); }
        finally { _storage.Db.Aop.OnLogExecuting = null; }

        var pending = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().SingleAsync();
        Assert.Equal(0, pending.ProcessedGeneration);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Contains("Injected completion failure", pending.LastError);
        Assert.Equal(originalPoints, (await _storage.Db.Queryable<PlayerEntity>().SingleAsync()).Points);
        Assert.Equal(originalPoints, (await _storage.Db.Queryable<PlayerTrackScoreEntity>().SingleAsync()).Points);

        await _storage.ProcessScoreRecalcOutboxBatchAsync(pending.AvailableAtUtc, "completion-retry");
        var done = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().SingleAsync();
        Assert.Equal(done.RequestedGeneration, done.ProcessedGeneration);
        Assert.Null(done.PendingSinceUtc);
        Assert.Equal(originalPoints * 2, (await _storage.Db.Queryable<PlayerEntity>().SingleAsync()).Points);
    }

    [Fact]
    public async Task RecalculationFailureRollsBackTrackScoresAndRetryRepairsTotal()
    {
        var map = await _storage.GetMapInfo("surf_scores");
        await _storage.GetPlayerProfile(Player, "Player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, Time = 80,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).ExecuteCommandAsync();
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.StartsWith("UPDATE `surf_players`")) throw new InvalidOperationException("Injected total update failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1));
        Assert.Equal(0, await _storage.Db.Queryable<PlayerTrackScoreEntity>().CountAsync());
        _storage.Db.Aop.OnLogExecuting = null;
        await _storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var expected = (await _storage.Db.Queryable<PlayerTrackScoreEntity>().FirstAsync()).Points;
        Assert.Equal(expected, (await _storage.Db.Queryable<PlayerEntity>().FirstAsync()).Points);
    }

    [Fact]
    public async Task BestRunInsertFailureRollsBackTheRunInsteadOfReportingSuccess()
    {
        await _storage.GetMapInfo("surf_failure");
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("INSERT INTO `surf_player_best_runs`")) throw new InvalidOperationException("Injected best-row failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.AddPlayerStageRecord(Player, "surf_failure", new RecordRequest { Time = 80, Stage = 1 }));
        Assert.Equal(0, await _storage.Db.Queryable<RunEntity>().CountAsync());
    }

    [Fact]
    public async Task PlaytimeInsertFailureIsNotSilentlyLost()
    {
        await _storage.GetMapInfo("surf_stats_failure");
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("INSERT INTO `surf_player_map_stats`")) throw new InvalidOperationException("Injected stats failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => _storage.UpdatePlayerMapStatsAsync(Player, "surf_stats_failure", 60));
    }

    [Theory]
    [InlineData(76561198000000001UL)]
    [InlineData(ulong.MaxValue)]
    public async Task SteamIdRoundTripsThroughPrimitiveDatabaseColumns(ulong value)
    {
        var steamId = new SteamID(value);
        var profile = await _storage.GetPlayerProfile(steamId, "Round trip");
        Assert.True(profile.Id > 0);
        Assert.Equal(value, profile.SteamId.AsPrimitive());
        await _storage.AddPlayerRecord(steamId, "surf_steam_id", new RecordRequest { Time = 80 });
        Assert.Equal(value, Assert.Single(await _storage.GetPlayerRecords(steamId, "surf_steam_id")).SteamId);
        Assert.Equal(value, Assert.Single(await _storage.GetRecentRecords("surf_steam_id", steamId)).SteamId);
        await _storage.UpdatePlayerMapStatsAsync(steamId, "surf_steam_id", 60);
        Assert.Equal((60f, 1), await _storage.GetPlayerMapStatsAsync(steamId, "surf_steam_id"));
    }

    [Fact]
    public async Task DurableRecalculationBacksOffThenRetriesAFailedTotalWrite()
    {
        var map = await _storage.GetMapInfo("surf_retry_scores");
        await _storage.GetPlayerProfile(Player, "Player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, Time = 80,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).ExecuteCommandAsync();

        var requestedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await _storage.EnqueueScoreRecalcAsync(map.MapId, 0, 0, 1, requestedAt);

        var totalAttempts = 0;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.StartsWith("UPDATE `surf_players`") && ++totalAttempts == 1)
                throw new InvalidOperationException("Fail first total update");
        };

        var firstEligibleAt = requestedAt.AddSeconds(5);
        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(firstEligibleAt, "first-worker"));

        var failed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().SingleAsync();
        Assert.Equal(1, totalAttempts);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Null(failed.LeaseOwner);
        Assert.Null(failed.LeaseUntilUtc);
        Assert.Null(failed.DeadLetteredAtUtc);
        Assert.Equal(firstEligibleAt.AddSeconds(1), failed.AvailableAtUtc);
        Assert.Equal(0, await _storage.Db.Queryable<PlayerTrackScoreEntity>().CountAsync());

        Assert.Equal(0, await _storage.ProcessScoreRecalcOutboxBatchAsync(firstEligibleAt, "early-worker"));
        Assert.Equal(1, await _storage.ProcessScoreRecalcOutboxBatchAsync(failed.AvailableAtUtc, "retry-worker"));
        _storage.Db.Aop.OnLogExecuting = null;

        var completed = await _storage.Db.Queryable<ScoreRecalcOutboxEntity>().SingleAsync();
        Assert.Equal(2, totalAttempts);
        Assert.Equal(completed.RequestedGeneration, completed.ProcessedGeneration);
        Assert.Equal(0, completed.AttemptCount);
        Assert.Null(completed.LeaseOwner);
        Assert.Equal(1000u, (await _storage.Db.Queryable<PlayerEntity>().FirstAsync()).Points);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ReplaySelectionUsesEarliestRunForEqualTimes(int stage)
    {
        var map = await _storage.GetMapInfo("surf_replay");
        // Reverse insertion order and player IDs to ensure neither chooses the winner.
        foreach (var id in new ulong[] { 20, 10 })
        {
            var player = new SteamID(Player.AsPrimitive() + (20 - id));
            await _storage.Db.Insertable(new RunEntity
            {
                Id = id, MapId = map.MapId, SteamId = unchecked((long)player.AsPrimitive()), Time = 80, Stage = (ushort)stage,
                RunType = stage == 0 ? RunType.Main : RunType.Stage,
                DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
            }).OffIdentity().ExecuteCommandAsync();
            await _storage.Db.Insertable(new ReplayEntity
            {
                MapId = map.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunId = id, Replay = $"replay-{id}",
                CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), UpdatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }).ExecuteCommandAsync();
        }
        var url = await _storage.GetReplayUrlAsync("surf_replay", stage == 0 ? RunType.Main : RunType.Stage, 0, 0, stage, null);
        Assert.Equal("replay-10", url);
    }

    public void Dispose()
    {
        _storage.Shutdown();
        File.Delete(_path);
    }
}
