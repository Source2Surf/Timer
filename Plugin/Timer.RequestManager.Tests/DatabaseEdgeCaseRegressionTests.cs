using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Backend;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

[Collection(SqlSchemaMutationCollection.Name)]
public sealed class DatabaseEdgeCaseRegressionTests
{
    [Fact]
    public Task SqlitePlayerStateAndRankRegressions() => RunSuite(DbType.Sqlite, null);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task MySqlPlayerStateAndRankRegressions() => RunSuite(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task PostgreSqlPlayerStateAndRankRegressions() => RunSuite(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task RunSuite(DbType type, string? variable)
    {
        using var fixture = new Fixture(type, variable);
        await InvalidDeltasDoNotCreateRows(fixture);
        await CountersRejectUnrepresentableTotalsAtomically(fixture);
        await JoinDatesRemainStableAcrossRenamesAndScores(fixture);
        foreach (var time in new[] { 80f, 80.1f, MathF.BitIncrement(80f) })
            await EqualTimeRanksFollowTheScoreOrder(fixture, time);
        await ImprovedBestUpdatesOwnRowBesideOtherPlayers(fixture);
        await LegacyJoinDateMigrationIsAdditiveAndIdempotent(fixture);
        await InvalidHistoricalTimesCanBeRepaired(fixture);
        if (type != DbType.Sqlite) await ConcurrentWritesPreserveJoinDatesAndCounters(fixture);
    }

    private static async Task InvalidDeltasDoNotCreateRows(Fixture f)
    {
        foreach (var delta in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1f, float.MaxValue })
        {
            var playerMap = Fixture.MapName();
            var globalMap = Fixture.MapName();
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                f.Store.UpdatePlayerMapStatsAsync(Fixture.Player(), playerMap, delta));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                f.Store.IncrementMapStatsAsync(globalMap, delta));
            Assert.False(await f.Store.Db.Queryable<MapEntity>().Where(x => x.File == playerMap || x.File == globalMap).AnyAsync());
        }

        var zeroMap = Fixture.MapName();
        await f.Store.UpdatePlayerMapStatsAsync(Fixture.Player(), zeroMap, 0f);
        Assert.False(await f.Store.Db.Queryable<MapEntity>().Where(x => x.File == zeroMap).AnyAsync());
    }

    private static async Task CountersRejectUnrepresentableTotalsAtomically(Fixture f)
    {
        var id = Fixture.Player();
        var steamId = checked((long)id.AsPrimitive());
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        await f.Store.UpdatePlayerMapStatsAsync(id, map.MapName, 60);
        await f.Store.Db.Updateable<PlayerMapStatsEntity>()
            .SetColumns(x => x.PlayCount == int.MaxValue)
            .Where(x => x.SteamId == steamId && x.MapId == map.MapId).ExecuteCommandAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.UpdatePlayerMapStatsAsync(id, map.MapName, 30));
        Assert.Equal((60f, int.MaxValue), await f.Store.GetPlayerMapStatsAsync(id, map.MapName));

        await f.Store.Db.Updateable<MapEntity>().SetColumns(x => x.PlayCount == int.MaxValue)
            .Where(x => x.MapId == map.MapId).ExecuteCommandAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.IncrementMapStatsAsync(map.MapName, 30));
        var full = await f.Store.Db.Queryable<MapEntity>().Where(x => x.MapId == map.MapId).SingleAsync();
        Assert.Equal(int.MaxValue, full.PlayCount);
        Assert.Equal(0f, full.TotalPlayTime);

        var timeMap = await f.Store.GetMapInfo(Fixture.MapName());
        var half = StorageServiceImpl.MaximumStoredPlayTimeSeconds / 2;
        await f.Store.UpdatePlayerMapStatsAsync(id, timeMap.MapName, half);
        await f.Store.UpdatePlayerMapStatsAsync(id, timeMap.MapName, half);
        await f.Store.IncrementMapStatsAsync(timeMap.MapName, half);
        await f.Store.IncrementMapStatsAsync(timeMap.MapName, half);
        var playerBefore = await f.Store.GetPlayerMapStatsAsync(id, timeMap.MapName);
        var mapBefore = await f.Store.Db.Queryable<MapEntity>().Where(x => x.MapId == timeMap.MapId).SingleAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.UpdatePlayerMapStatsAsync(id, timeMap.MapName, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.IncrementMapStatsAsync(timeMap.MapName, 1));
        Assert.Equal(playerBefore, await f.Store.GetPlayerMapStatsAsync(id, timeMap.MapName));
        var limit = await f.Store.Db.Queryable<MapEntity>().Where(x => x.MapId == timeMap.MapId).SingleAsync();
        Assert.Equal(2, playerBefore.playCount);
        Assert.Equal(2, limit.PlayCount);
        Assert.Equal(mapBefore.TotalPlayTime, limit.TotalPlayTime);
        // MySQL FLOAT's text protocol can round by one ULP on read. The invariant
        // here is no write after rejection, and every materialized time fits the API.
        var lower = MathF.BitDecrement(StorageServiceImpl.MaximumStoredPlayTimeSeconds);
        Assert.InRange(playerBefore.playTime, lower, StorageServiceImpl.MaximumStoredPlayTimeSeconds);
        Assert.InRange(limit.TotalPlayTime, lower, StorageServiceImpl.MaximumStoredPlayTimeSeconds);
        Assert.True(checked((long)Math.Round((double)limit.TotalPlayTime * 1_000_000)) > 0);
    }

    private static async Task JoinDatesRemainStableAcrossRenamesAndScores(Fixture f)
    {
        var player = Fixture.Player();
        var id = checked((long)player.AsPrimitive());
        var first = await Profile(f.Store, id, "First");
        Assert.Equal(first.JoinDateUtc, (await Profile(f.Store, id, "First")).JoinDateUtc);
        var changed = await Profile(f.Store, id, "Changed");
        Assert.Equal(first.JoinDateUtc, changed.JoinDateUtc);
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 80 });
        await f.Store.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var scored = await Profile(f.Store, id, "Changed");
        Assert.Equal(1000u, scored.Points);
        Assert.Equal(first.JoinDateUtc, scored.JoinDateUtc);

        // A row inserted by a previous writer may still have a null join date.
        var legacy = Fixture.Player();
        var legacyId = checked((long)legacy.AsPrimitive());
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await f.Store.Db.Insertable(new PlayerEntity { SteamId = legacyId, Name = "Legacy", UpdatedAt = old }).ExecuteCommandAsync();
        var legacyMap = await f.Store.GetMapInfo(Fixture.MapName());
        await f.Store.AddPlayerRecord(legacy, legacyMap.MapName, new RecordRequest { Time = 80 });
        await f.Store.RecalculateTrackScoresAsync(legacyMap.MapId, 0, 0, 1);
        var initialized = await Profile(f.Store, legacyId, "Renamed legacy");
        Assert.Equal(old, initialized.JoinDateUtc);
        Assert.Equal(old, (await Profile(f.Store, legacyId, "Renamed again")).JoinDateUtc);
    }

    private static async Task EqualTimeRanksFollowTheScoreOrder(Fixture f, float time)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var firstPlayer = Fixture.Player();
        var secondPlayer = Fixture.Player();
        await Profile(f.Store, checked((long)firstPlayer.AsPrimitive()), "First tie");
        await Profile(f.Store, checked((long)secondPlayer.AsPrimitive()), "Second tie");
        var first = await f.Store.AddPlayerRecord(firstPlayer, map.MapName, new RecordRequest { Time = time });
        var second = await f.Store.AddPlayerRecord(secondPlayer, map.MapName, new RecordRequest { Time = time });
        Assert.Equal(1, first.rank);
        Assert.Equal(2, second.rank);
        await f.Store.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var a = await Profile(f.Store, checked((long)firstPlayer.AsPrimitive()), "First tie");
        var b = await Profile(f.Store, checked((long)secondPlayer.AsPrimitive()), "Second tie");
        Assert.Equal(1000u, a.Points);
        Assert.Equal(431u, b.Points);
        var stageFirst = await f.Store.AddPlayerStageRecord(firstPlayer, map.MapName, new RecordRequest { Stage = 1, Time = time / 4 });
        var stageSecond = await f.Store.AddPlayerStageRecord(secondPlayer, map.MapName, new RecordRequest { Stage = 1, Time = time / 4 });
        var differentStage = await f.Store.AddPlayerStageRecord(secondPlayer, map.MapName, new RecordRequest { Stage = 2, Time = time / 4 });
        Assert.Equal(1, stageFirst.rank);
        Assert.Equal(2, stageSecond.rank);
        Assert.Equal(1, differentStage.rank);
    }

    // PostgreSQL once read another player's row on the key as best-row id 0, so an improved
    // personal best tried to insert a second row and failed the unique index (23505).
    private static async Task ImprovedBestUpdatesOwnRowBesideOtherPlayers(Fixture f)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var other = Fixture.Player();
        var player = Fixture.Player();
        var steamId = checked((long)player.AsPrimitive());
        await Profile(f.Store, checked((long)other.AsPrimitive()), "Other");
        await Profile(f.Store, steamId, "Improver");

        await f.Store.AddPlayerRecord(other, map.MapName, new RecordRequest { Time = 30 });
        await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 90 });
        var improved = await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 85 });
        var slower = await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = 88 });
        Assert.Equal(EAttemptResult.NewPersonalRecord, improved.Item1);
        Assert.Equal(EAttemptResult.NoNewRecord, slower.Item1);

        await f.Store.AddPlayerStageRecord(other, map.MapName, new RecordRequest { Stage = 1, Time = 10 });
        await f.Store.AddPlayerStageRecord(player, map.MapName, new RecordRequest { Stage = 1, Time = 30 });
        var stageImproved = await f.Store.AddPlayerStageRecord(player, map.MapName, new RecordRequest { Stage = 1, Time = 25 });
        var stageSlower = await f.Store.AddPlayerStageRecord(player, map.MapName, new RecordRequest { Stage = 1, Time = 28 });
        Assert.Equal(EAttemptResult.NewPersonalRecord, stageImproved.Item1);
        Assert.Equal(EAttemptResult.NoNewRecord, stageSlower.Item1);

        var rows = await f.Store.Db.Queryable<PlayerBestRunEntity>()
                          .Where(x => x.MapId == map.MapId && x.SteamId == steamId)
                          .ToListAsync();
        Assert.Equal(2, rows.Count);
        var main = Assert.Single(rows, x => x.RunType == RunType.Main);
        var stage = Assert.Single(rows, x => x.RunType == RunType.Stage);
        Assert.Equal(checked((ulong)improved.Item2.Id), main.RunId);
        Assert.Equal(85f, main.BestTime);
        Assert.Equal(checked((ulong)stageImproved.Item2.Id), stage.RunId);
        Assert.Equal(25f, stage.BestTime);
    }

    private static async Task LegacyJoinDateMigrationIsAdditiveAndIdempotent(Fixture f)
    {
        var steamId = checked((long)Fixture.Player().AsPrimitive());
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await f.Store.Db.Insertable(new PlayerEntity { SteamId = steamId, Name = "Before migration", UpdatedAt = old }).ExecuteCommandAsync();
        // Dedicated disposable test database only. Reproduce the populated pre-upgrade shape.
        Assert.True(f.Store.Db.DbMaintenance.DropColumn("surf_players", nameof(PlayerEntity.JoinedAtUtc)));
        try
        {
            f.Store.MigratePlayerJoinDates();
            var row = await f.Store.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamId).SingleAsync();
            Assert.Equal(old, row.JoinedAtUtc);
            Assert.Equal(old, row.UpdatedAt);
            Assert.Equal("Before migration", row.Name);
            var later = old.AddYears(5);
            await f.Store.Db.Updateable<PlayerEntity>().SetColumns(x => x.UpdatedAt == later)
                .Where(x => x.SteamId == steamId).ExecuteCommandAsync();
            f.Store.MigratePlayerJoinDates();
            f.Store.Init(startScoreRecalcWorker: false);
            Assert.Equal(old, (await Profile(f.Store, steamId, "After migration")).JoinDateUtc);
            if (f.Type != DbType.Sqlite) await f.Store.MigrateMasterPointsColumnsAsync();
        }
        finally
        {
            // Restore schema even when an assertion or migration fails.
            if (!f.Store.Db.DbMaintenance.IsAnyColumn("surf_players", nameof(PlayerEntity.JoinedAtUtc), false))
                f.Store.MigratePlayerJoinDates();
        }
    }

    private static async Task InvalidHistoricalTimesCanBeRepaired(Fixture f)
    {
        var values = f.Type == DbType.PostgreSQL
            ? new[] { -1f, float.MaxValue / 2, float.PositiveInfinity, float.NaN }
            : f.Type == DbType.Sqlite ? new[] { -1f, float.MaxValue / 2, float.PositiveInfinity } : new[] { -1f, float.MaxValue / 2 };
        foreach (var value in values)
        {
            var player = Fixture.Player();
            var id = checked((long)player.AsPrimitive());
            var map = await f.Store.GetMapInfo(Fixture.MapName());
            await f.Store.Db.Insertable(new PlayerMapStatsEntity { SteamId = id, MapId = map.MapId, PlayTime = value, PlayCount = 7 }).ExecuteCommandAsync();
            await f.Store.Db.Updateable<MapEntity>().SetColumns(x => x.TotalPlayTime == value)
                .SetColumns(x => x.PlayCount == 8).Where(x => x.MapId == map.MapId).ExecuteCommandAsync();
            f.Store.RepairInvalidStoredPlayTimes();
            f.Store.RepairInvalidStoredPlayTimes();
            Assert.Equal((0f, 7), await f.Store.GetPlayerMapStatsAsync(player, map.MapName));
            var repaired = await f.Store.Db.Queryable<MapEntity>().Where(x => x.MapId == map.MapId).SingleAsync();
            Assert.Equal(0f, repaired.TotalPlayTime);
            Assert.Equal(8, repaired.PlayCount);
            await f.Store.UpdatePlayerMapStatsAsync(player, map.MapName, 60);
            await f.Store.IncrementMapStatsAsync(map.MapName, 60);
            Assert.Equal((60f, 8), await f.Store.GetPlayerMapStatsAsync(player, map.MapName));
        }
    }

    private static async Task ConcurrentWritesPreserveJoinDatesAndCounters(Fixture f)
    {
        var left = f.NewStore();
        var right = f.NewStore();
        var player = Fixture.Player();
        var id = checked((long)player.AsPrimitive());
        var profiles = await Task.WhenAll(Profile(left, id, "Concurrent"), Profile(right, id, "Concurrent"));
        Assert.Equal(profiles[0].PlayerId, profiles[1].PlayerId);
        Assert.Equal(profiles[0].JoinDateUtc, profiles[1].JoinDateUtc);
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        await Task.WhenAll(left.UpdatePlayerMapStatsAsync(player, map.MapName, 10),
                           right.UpdatePlayerMapStatsAsync(player, map.MapName, 20));
        Assert.Equal((30f, 2), await f.Store.GetPlayerMapStatsAsync(player, map.MapName));

        await f.Store.Db.Updateable<PlayerMapStatsEntity>()
            .SetColumns(x => x.PlayCount == int.MaxValue - 1)
            .Where(x => x.MapId == map.MapId && x.SteamId == id).ExecuteCommandAsync();
        var outcomes = await Task.WhenAll(Increment(left), Increment(right));
        Assert.Single(outcomes, succeeded => succeeded);
        Assert.Equal((31f, int.MaxValue), await f.Store.GetPlayerMapStatsAsync(player, map.MapName));

        async Task<bool> Increment(StorageServiceImpl store)
        {
            try { await store.UpdatePlayerMapStatsAsync(player, map.MapName, 1); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static Task<TimerBackendPlayerProfileResult> Profile(StorageServiceImpl store, long id, string name) =>
        store.EnsureBackendPlayerProfileAsync(new TimerBackendPlayerProfileCommand { SteamId = id, Name = name });

    private sealed class Fixture : IDisposable
    {
        private readonly string? _path;
        private readonly string _connection;
        private readonly List<StorageServiceImpl> _stores = [];
        internal DbType Type { get; }
        internal StorageServiceImpl Store { get; }
        internal Fixture(DbType type, string? variable)
        {
            Type = type;
            if (type == DbType.Sqlite)
            {
                _path = Path.Combine(Path.GetTempPath(), $"timer-edge-regression-{Guid.NewGuid():N}.db");
                _connection = $"Data Source={_path};Pooling=False";
            }
            else
            {
                _connection = Environment.GetEnvironmentVariable(variable!)!;
                var parsed = new DbConnectionStringBuilder { ConnectionString = _connection };
                var host = Convert.ToString(parsed[type == DbType.MySql ? "Server" : "Host"]);
                Assert.True(host is "127.0.0.1" or "localhost" or "::1"
                    && Convert.ToString(parsed["Database"])!.Contains("test", StringComparison.OrdinalIgnoreCase));
            }
            Store = NewStore();
            if (type != DbType.Sqlite) Store.Db.DbMaintenance.CreateDatabase();
            Store.Init(startScoreRecalcWorker: false);
        }
        internal StorageServiceImpl NewStore()
        {
            var store = new StorageServiceImpl(Type, _connection, NullLogger<StorageServiceImpl>.Instance, false);
            if (Type == DbType.Sqlite)
                store.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
                { if (column.IsIdentity) column.DataType = "INTEGER"; };
            _stores.Add(store);
            return store;
        }
        internal static SteamID Player() => new(76561198000000000UL + checked((ulong)Random.Shared.NextInt64(1, 1_000_000_000)));
        internal static string MapName() => $"surf_edge_{Guid.NewGuid():N}";
        public void Dispose()
        {
            foreach (var store in _stores) store.Shutdown();
            if (_path is not null && File.Exists(_path)) File.Delete(_path);
        }
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