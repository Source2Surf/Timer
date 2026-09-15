using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;
using Timer.RequestManager.Replay;
using Timer.RequestManager.Scheduling;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class StorageConsistencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-consistency-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;
    private static readonly SteamID Player = new(76561198000000001);

    public StorageConsistencyTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False", NullLogger<StorageServiceImpl>.Instance);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Db.CodeFirst.InitTables(typeof(MapEntity), typeof(MapTrackEntity), typeof(PlayerEntity), typeof(RunEntity),
                                        typeof(PlayerBestRunEntity), typeof(PlayerTrackScoreEntity), typeof(PlayerMapStatsEntity), typeof(ReplayEntity));
    }

    [Fact]
    public async Task RecalculationFailureRollsBackTrackScoresAndRetryRepairsTotal()
    {
        var map = await _storage.GetMapInfo("surf_scores");
        await _storage.GetPlayerProfile(Player, "Player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, Time = 80, Date = DateTime.UtcNow,
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
    public async Task ScheduledRecalculationRetriesAFailedTotalWrite()
    {
        var map = await _storage.GetMapInfo("surf_retry_scores");
        await _storage.GetPlayerProfile(Player, "Player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, Time = 80, Date = DateTime.UtcNow,
        }).ExecuteCommandAsync();
        var totalAttempts = 0;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.StartsWith("UPDATE `surf_players`") && ++totalAttempts == 1)
                throw new InvalidOperationException("Fail first total update");
        };
        var handler = typeof(StorageServiceImpl).GetMethod("HandleScoreRecalcAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)handler.Invoke(_storage, [new RecalcRequest(map.MapId, 0, 0, 1)])!;
        Assert.Equal(2, totalAttempts);
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
                RunType = stage == 0 ? RunType.Main : RunType.Stage, Date = DateTime.UtcNow,
            }).OffIdentity().ExecuteCommandAsync();
            await _storage.Db.Insertable(new ReplayEntity
            {
                MapId = map.MapId, SteamId = unchecked((long)player.AsPrimitive()), RunId = id, Replay = $"replay-{id}",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            }).ExecuteCommandAsync();
        }
        var provider = new DbReplayProvider(_storage, new TestReplayStorage(), false, NullLogger<DbReplayProvider>.Instance);
        var data = stage == 0 ? await provider.GetReplayAsync("surf_replay", 0, 0)
                              : await provider.GetStageReplayAsync("surf_replay", 0, 0, stage);
        Assert.Equal("replay-10", Encoding.UTF8.GetString(data!));
    }

    private sealed class TestReplayStorage : IReplayStorage
    {
        public Task<byte[]> DownloadAsync(string url) => Task.FromResult(Encoding.UTF8.GetBytes(url));
        public Task<string> UploadAsync(string key, byte[] data) => throw new NotSupportedException();
        public Task DeleteAsync(string url) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        _storage.Shutdown();
        File.Delete(_path);
    }
}
