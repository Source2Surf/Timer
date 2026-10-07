using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

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
        foreach (var time in new[] { 123.46875f, 10000.03125f })
            await TiesNeverBeatTheStoredTime(fixture, time);
        foreach (var time in new[] { 1234.578125f, 10000.03125f })
            await TimesReadBackExactlyThroughTheirTicks(fixture, time);
        await TicksFollowAHandEditedTime(fixture);
        await LegacyDatesMoveToUnixMilliseconds(fixture);
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
        await f.Store.Db.Insertable(new PlayerEntity { SteamId = legacyId, Name = "Legacy", UpdatedAtUnixMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(old) })
               .ExecuteCommandAsync();
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

    // MySQL returns FLOAT rounded to 6 digits (123.46875 -> 123.469), so a tie compared in C# beat the stored time.
    private static async Task TiesNeverBeatTheStoredTime(Fixture f, float time)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var holder = Fixture.Player();
        var challenger = Fixture.Player();
        var holderId = checked((long)holder.AsPrimitive());
        await Profile(f.Store, holderId, "Holder");
        await Profile(f.Store, checked((long)challenger.AsPrimitive()), "Challenger");

        var record = await f.Store.AddPlayerRecord(holder, map.MapName, new RecordRequest { Time = time });
        var tie = await f.Store.AddPlayerRecord(challenger, map.MapName, new RecordRequest { Time = time });
        var repeat = await f.Store.AddPlayerRecord(holder, map.MapName, new RecordRequest { Time = time });
        Assert.Equal(EAttemptResult.NewServerRecord, record.Item1);
        Assert.Equal(EAttemptResult.NewPersonalRecord, tie.Item1);
        Assert.Equal(EAttemptResult.NoNewRecord, repeat.Item1);

        var stageRecord = await f.Store.AddPlayerStageRecord(holder, map.MapName, new RecordRequest { Stage = 1, Time = time });
        var stageTie = await f.Store.AddPlayerStageRecord(challenger, map.MapName, new RecordRequest { Stage = 1, Time = time });
        var stageRepeat = await f.Store.AddPlayerStageRecord(holder, map.MapName, new RecordRequest { Stage = 1, Time = time });
        Assert.Equal(EAttemptResult.NewServerRecord, stageRecord.Item1);
        Assert.Equal(EAttemptResult.NewPersonalRecord, stageTie.Item1);
        Assert.Equal(EAttemptResult.NoNewRecord, stageRepeat.Item1);

        var bests = await f.Store.Db.Queryable<PlayerBestRunEntity>()
                           .Where(x => x.MapId == map.MapId && x.SteamId == holderId)
                           .ToListAsync();
        Assert.Equal(checked((ulong)record.Item2.Id), Assert.Single(bests, x => x.RunType == RunType.Main).RunId);
        Assert.Equal(checked((ulong)stageRecord.Item2.Id), Assert.Single(bests, x => x.RunType == RunType.Stage).RunId);
    }

    // MySQL hands a FLOAT back rounded to 6 digits (1234.578125 -> 1234.58); the ticks stored beside it are exact.
    private static async Task TimesReadBackExactlyThroughTheirTicks(Fixture f, float time)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var player = Fixture.Player();
        var steamId = checked((long)player.AsPrimitive());
        var ticks = checked((int)(time * 64));
        await Profile(f.Store, steamId, "Long");

        await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest { Time = time });
        await f.Store.AddPlayerStageRecord(player, map.MapName, new RecordRequest { Stage = 1, Time = time });

        Assert.All(await f.Store.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).ToListAsync(), x => Assert.Equal(ticks, x.Ticks));
        Assert.All(await f.Store.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).ToListAsync(),
                   x => Assert.Equal(ticks, x.BestTicks));

        Assert.Equal(time, Assert.Single(await f.Store.GetMapRecords(map.MapName, 0, 0)).Time);
        Assert.Equal(time, Assert.Single(await f.Store.GetPlayerRecords(player, map.MapName)).Time);
        Assert.Equal(time, Assert.Single(await f.Store.GetPlayerStageRecords(player, map.MapName)).Time);
        Assert.Equal(time, Assert.Single(await f.Store.GetMapStageRecords(map.MapName)).Time);
    }

    // The stored time is the authority: an edited one shows as it is, and startup brings its ticks back in line,
    // like it fills in the ticks of runs from before they were kept.
    private static async Task TicksFollowAHandEditedTime(Fixture f)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var edited = Fixture.Player();
        var legacy = Fixture.Player();
        await Profile(f.Store, checked((long)edited.AsPrimitive()), "Edited");
        await Profile(f.Store, checked((long)legacy.AsPrimitive()), "Legacy");
        await f.Store.AddPlayerRecord(edited, map.MapName, new RecordRequest { Time = 90 });
        await f.Store.AddPlayerRecord(legacy, map.MapName, new RecordRequest { Time = 95 });

        var editedId = checked((long)edited.AsPrimitive());
        var legacyId = checked((long)legacy.AsPrimitive());
        await f.Store.Db.Updateable<RunEntity>().SetColumns(x => x.Time == 85.5f).Where(x => x.SteamId == editedId).ExecuteCommandAsync();
        await f.Store.Db.Updateable<PlayerBestRunEntity>().SetColumns(x => x.BestTime == 85.5f).Where(x => x.SteamId == editedId).ExecuteCommandAsync();
        await f.Store.Db.Updateable<RunEntity>().SetColumns(x => x.Ticks == 0).Where(x => x.SteamId == legacyId).ExecuteCommandAsync();
        await f.Store.Db.Updateable<PlayerBestRunEntity>().SetColumns(x => x.BestTicks == 0).Where(x => x.SteamId == legacyId).ExecuteCommandAsync();

        Assert.Equal([85.5f, 95f], (await f.Store.GetMapRecords(map.MapName, 0, 0)).Select(x => x.Time));

        f.Store.SyncTicks();

        var runs = await f.Store.Db.Queryable<RunEntity>().Where(x => x.MapId == map.MapId).ToListAsync();
        var bests = await f.Store.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).ToListAsync();
        Assert.Equal(5472, Assert.Single(runs, x => x.SteamId == editedId).Ticks);
        Assert.Equal(6080, Assert.Single(runs, x => x.SteamId == legacyId).Ticks);
        Assert.Equal(5472, Assert.Single(bests, x => x.SteamId == editedId).BestTicks);
        Assert.Equal(6080, Assert.Single(bests, x => x.SteamId == legacyId).BestTicks);
        Assert.Equal([85.5f, 95f], (await f.Store.GetMapRecords(map.MapName, 0, 0)).Select(x => x.Time));
    }

    // Older databases kept these dates as SQL timestamps, without a time zone. Startup moves them into the unix-ms
    // columns and drops them; a player without a join date joined when last updated.
    private static async Task LegacyDatesMoveToUnixMilliseconds(Fixture f)
    {
        var map = await f.Store.GetMapInfo(Fixture.MapName());
        var player = Fixture.Player();
        var steamId = checked((long)player.AsPrimitive());
        var legacy = checked((long)Fixture.Player().AsPrimitive());
        await Profile(f.Store, steamId, "Before migration");
        await f.Store.Db.Insertable(new PlayerEntity { SteamId = legacy, Name = "No join date" }).ExecuteCommandAsync();
        var (_, run, _) = await f.Store.AddPlayerRecord(player, map.MapName, new RecordRequest
        {
            Time = 80, Checkpoints = [new () { CheckpointIndex = 1, Time = 40 }],
        });
        await f.Store.Db.Insertable(new ReplayEntity { MapId = map.MapId, SteamId = steamId, RunId = checked((ulong)run.Id), Replay = "legacy.replay" })
               .ExecuteCommandAsync();

        var updated = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var joined  = new DateTime(2019, 6, 7, 8, 9, 10, DateTimeKind.Utc); // whole seconds: MySQL DATETIME drops milliseconds

        // Dedicated disposable test database only. Reproduce the pre-upgrade shape: timestamps beside zeroed new columns.
        var timestamp = f.Type switch { DbType.PostgreSQL => "timestamp", _ => "datetime" };
        foreach (var (table, column) in LegacyDateColumns)
        {
            Assert.True(f.Store.Db.DbMaintenance.AddColumn(table, new DbColumnInfo { DbColumnName = column, DataType = timestamp, IsNullable = true }));
        }

        await f.Store.Db.Updateable<PlayerEntity>().SetColumns(x => x.JoinedAtUnixMilliseconds == 0).SetColumns(x => x.UpdatedAtUnixMilliseconds == 0)
               .Where(x => x.SteamId == steamId || x.SteamId == legacy).ExecuteCommandAsync();
        await f.Store.Db.Updateable(new LegacyPlayerDates { Id = (await PlayerRow(f, steamId)).Id, UpdatedAt = updated, JoinedAtUtc = joined })
               .ExecuteCommandAsync();
        await f.Store.Db.Updateable(new LegacyPlayerDates { Id = (await PlayerRow(f, legacy)).Id, UpdatedAt = updated }).ExecuteCommandAsync();
        await f.Store.Db.Updateable<LegacyBestRunDates>().SetColumns(x => x.UpdatedAt == updated).Where(x => x.MapId == map.MapId).ExecuteCommandAsync();
        await f.Store.Db.Updateable<LegacySegmentDates>().SetColumns(x => x.Date == updated).Where(x => x.RunId == checked((ulong)run.Id)).ExecuteCommandAsync();
        await f.Store.Db.Updateable<LegacyReplayDates>().SetColumns(x => x.CreatedAt == joined).SetColumns(x => x.UpdatedAt == updated)
               .Where(x => x.RunId == checked((ulong)run.Id)).ExecuteCommandAsync();

        f.Store.MigrateLegacyDates();
        f.Store.MigrateLegacyDates(); // a repeat does nothing

        var updatedMs = StorageServiceImpl.ToUnixTimeMilliseconds(updated);
        var joinedMs  = StorageServiceImpl.ToUnixTimeMilliseconds(joined);
        var moved     = await PlayerRow(f, steamId);
        Assert.Equal((joinedMs, updatedMs), (moved.JoinedAtUnixMilliseconds, moved.UpdatedAtUnixMilliseconds));
        Assert.Equal(updatedMs, (await PlayerRow(f, legacy)).JoinedAtUnixMilliseconds);
        Assert.Equal(updatedMs, (await f.Store.Db.Queryable<PlayerBestRunEntity>().Where(x => x.MapId == map.MapId).SingleAsync()).UpdatedAtUnixMilliseconds);
        Assert.Equal(updatedMs, (await f.Store.Db.Queryable<RunSegmentEntity>().Where(x => x.RunId == checked((ulong)run.Id)).SingleAsync()).DateUnixMilliseconds);
        var replay = await f.Store.Db.Queryable<ReplayEntity>().Where(x => x.RunId == checked((ulong)run.Id)).SingleAsync();
        Assert.Equal((joinedMs, updatedMs), (replay.CreatedAtUnixMilliseconds, replay.UpdatedAtUnixMilliseconds));

        foreach (var (table, column) in LegacyDateColumns)
        {
            Assert.False(f.Store.Db.DbMaintenance.IsAnyColumn(table, column, false), $"{table}.{column}");
        }

        Assert.Equal(joined, (await Profile(f.Store, steamId, "After migration")).JoinDateUtc);
    }

    private static readonly (string Table, string Column)[] LegacyDateColumns =
    [
        ("surf_players", "UpdatedAt"), ("surf_players", "JoinedAtUtc"), ("surf_player_best_runs", "UpdatedAt"),
        ("surf_runs_segments", "Date"), ("surf_runs_replay", "CreatedAt"), ("surf_runs_replay", "UpdatedAt"),
    ];

    private static Task<PlayerEntity> PlayerRow(Fixture f, long steamId)
        => f.Store.Db.Queryable<PlayerEntity>().Where(x => x.SteamId == steamId).SingleAsync();

    [SugarTable("surf_players")]
    private sealed class LegacyPlayerDates
    {
        [SugarColumn(IsPrimaryKey = true)] public ulong Id { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? JoinedAtUtc { get; set; }
    }

    [SugarTable("surf_player_best_runs")]
    private sealed class LegacyBestRunDates
    {
        [SugarColumn(IsPrimaryKey = true)] public ulong Id { get; set; }
        public ulong MapId { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }

    [SugarTable("surf_runs_segments")]
    private sealed class LegacySegmentDates
    {
        [SugarColumn(IsPrimaryKey = true)] public ulong Id { get; set; }
        public ulong RunId { get; set; }
        public DateTime? Date { get; set; }
    }

    [SugarTable("surf_runs_replay")]
    private sealed class LegacyReplayDates
    {
        [SugarColumn(IsPrimaryKey = true, ColumnDataType = "bigint")] public long SteamId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong MapId { get; set; }
        [SugarColumn(IsPrimaryKey = true)] public ulong RunId { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
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