using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.RegularExpressions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;
using Xunit.Abstractions;

namespace Timer.RequestManager.Tests;

public sealed class BestRunSeedingTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-best-runs-{Guid.NewGuid():N}.db");
    private StorageServiceImpl? _storage;
    private static readonly SteamID Player = new(76561198000000001);

    private StorageServiceImpl CreateStorage()
    {
        var storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False", NullLogger<StorageServiceImpl>.Instance);
        storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        storage.Db.CodeFirst.InitTables(typeof(MapEntity),
                                        typeof(RunEntity),
                                        typeof(PlayerBestRunEntity),
                                        typeof(PlayerEntity),
                                        typeof(MapTrackEntity),
                                        typeof(ScoreRecalcOutboxEntity));
        storage.Db.Aop.OnLogExecuting = (sql, _) => output.WriteLine(sql);
        return _storage = storage;
    }

    private static async Task<RunEntity> AddRun(StorageServiceImpl storage, ulong mapId, float time, int style = 0, ushort track = 0,
                                               ushort stage = 0, SteamID? player = null)
    {
        var run = new RunEntity
        {
            MapId = mapId, SteamId = unchecked((long)(player ?? Player).AsPrimitive()), Time = time, Style = style, Track = track, Stage = stage,
            RunType = stage == 0 ? RunType.Main : RunType.Stage,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        };
        run.Id = (ulong)await storage.Db.Insertable(run).ExecuteReturnBigIdentityAsync();
        return run;
    }

    [Fact]
    public async Task MapSeedSelectsFastestRunPerPlayerStyleTrackAndStage()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_seed");
        await AddRun(storage, map.MapId, 120);
        var best = await AddRun(storage, map.MapId, 80);
        await AddRun(storage, map.MapId, 80); // Equal time keeps the earlier run.
        await AddRun(storage, map.MapId, 100);
        var styleBest = await AddRun(storage, map.MapId, 50, style: 1);
        var bonusBest = await AddRun(storage, map.MapId, 20, track: 1);
        var stageBest = await AddRun(storage, map.MapId, 10, stage: 1);
        await AddRun(storage, map.MapId, 15, stage: 1);
        var stageTwoBest = await AddRun(storage, map.MapId, 8, stage: 2);
        var otherBest = await AddRun(storage, map.MapId, 70, player: new SteamID(76561198000000002));
        var otherMap = await storage.GetMapInfo("surf_other");
        await AddRun(storage, otherMap.MapId, 1);

        var records = await storage.GetPlayerRecords(Player, "surf_seed");
        Assert.Equal(new[] { best.Id, styleBest.Id, bonusBest.Id }.Order(), records.Select(x => (ulong)x.Id).Order());
        var stages = await storage.GetPlayerStageRecords(Player, "surf_seed");
        Assert.Equal(new[] { (long)stageBest.Id, (long)stageTwoBest.Id }, stages.Select(x => x.Id));
        Assert.Equal((long)otherBest.Id, (await storage.GetPlayerRecord(new SteamID(unchecked((ulong)otherBest.SteamId)), "surf_seed", 0, 0))!.Id);
    }

    [Fact]
    public async Task ScopedSeedRepairsPartialBestTable()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_partial");
        var slow = await AddRun(storage, map.MapId, 120);
        var best = await AddRun(storage, map.MapId, 80);
        var other = await AddRun(storage, map.MapId, 90, player: new SteamID(76561198000000002));
        await storage.Db.Insertable(new PlayerBestRunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, RunId = slow.Id, BestTime = slow.Time,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        Assert.Equal((long)best.Id, (await storage.GetPlayerRecord(Player, "surf_partial", 0, 0))!.Id);
        Assert.Equal((long)other.Id, (await storage.GetPlayerRecord(new SteamID(unchecked((ulong)other.SteamId)), "surf_partial", 0, 0))!.Id);
    }

    [Theory]
    [InlineData(DbType.MySql)]
    [InlineData(DbType.PostgreSQL)]
    [InlineData(DbType.Sqlite)]
    public void SeedWindowUsesColumnsInsteadOfConstantParameters(DbType dialect)
    {
        using var db = new SqlSugarClient(new ConnectionConfig
        {
            DbType = dialect, InitKeyType = InitKeyType.Attribute,
            ConnectionString = dialect == DbType.Sqlite ? "Data Source=:memory:" : "Server=localhost;Database=unused",
        });
        var method = typeof(StorageServiceImpl).GetMethod("QuerySeedBestRows", BindingFlags.NonPublic | BindingFlags.Static)!;
        var query = method.Invoke(null, [db.Queryable<RunEntity>()])!;
        var sql = ((KeyValuePair<string, List<SugarParameter>>)query.GetType().GetMethod("ToSql", Type.EmptyTypes)!.Invoke(query, null)!).Key;
        output.WriteLine(sql);
        var window = Regex.Match(sql, @"over\s*\(([^)]*)\)", RegexOptions.IgnoreCase).Groups[1].Value;
        Assert.DoesNotContain("@", window);
        var normalized = Regex.Replace(window.Replace("`", "").Replace("\"", ""), @"\s+", " ").Trim().ToLowerInvariant();
        Assert.Equal("partition by style, track, stage, steamid order by time asc, id asc", normalized);
    }

    [Fact]
    public async Task StaleSeedCannotOverwriteAFasterFinish()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_race");
        var slow = await AddRun(storage, map.MapId, 120);
        await AddRun(storage, map.MapId, 80);
        await storage.Db.Insertable(new PlayerBestRunEntity
        {
            MapId = map.MapId, SteamId = unchecked((long)Player.AsPrimitive()), RunType = RunType.Main, RunId = slow.Id, BestTime = slow.Time,
            UpdatedAt = DateTime.UtcNow,
        }).ExecuteCommandAsync();

        var faster = await AddRun(storage, map.MapId, 60);
        await storage.Db.Updateable<PlayerBestRunEntity>()
            .SetColumns(x => x.RunId == faster.Id).SetColumns(x => x.BestTime == 60)
            .Where(x => x.MapId == map.MapId).ExecuteCommandAsync();
        var record = Assert.Single(await storage.GetPlayerRecords(Player, "surf_race"));
        Assert.Equal((long)faster.Id, record.Id);
        Assert.Equal(60, record.Time);
    }

    [Fact]
    public async Task ReadApiDoesNotRepairBestRunProjectionUnlessExplicitlyEnabled()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_read_only");
        var best = await AddRun(storage, map.MapId, 80);

        var readOnlyResult = await storage.GetMapRecordsForReadApiAsync("surf_read_only",
                                                                        stageRecords: false,
                                                                        style: null,
                                                                        track: null,
                                                                        stage: null,
                                                                        limit: 10,
                                                                        allowReadRepair: false);

        Assert.Empty(readOnlyResult);
        Assert.Equal(0, await storage.Db.Queryable<PlayerBestRunEntity>().CountAsync());

        var repairedResult = await storage.GetMapRecordsForReadApiAsync("surf_read_only",
                                                                        stageRecords: false,
                                                                        style: null,
                                                                        track: null,
                                                                        stage: null,
                                                                        limit: 10,
                                                                        allowReadRepair: true);

        Assert.Equal((long)best.Id, Assert.Single(repairedResult).Id);
        Assert.Equal(1, await storage.Db.Queryable<PlayerBestRunEntity>().CountAsync());
    }

    [Fact]
    public async Task PlayerReadApiAppliesLimitInSql()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_player_limit");
        await AddRun(storage, map.MapId, 80, style: 0);
        await AddRun(storage, map.MapId, 70, style: 1);
        await AddRun(storage, map.MapId, 60, style: 2);

        var records = await storage.GetPlayerRecordsForReadApiAsync(Player.AsPrimitive(),
                                                                     "surf_player_limit",
                                                                     stageRecords: false,
                                                                     limit: 2,
                                                                     allowReadRepair: true);

        Assert.Equal(new[] { 60f, 70f }, records.Select(record => record.Time));
    }

    [Fact]
    public async Task PlayerReadApiIncludesPlayerNames()
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_player_names");
        await storage.GetPlayerProfile(Player, "Player One");
        await AddRun(storage, map.MapId, 80);
        await AddRun(storage, map.MapId, 10, stage: 1);

        var main = await storage.GetPlayerRecordsForReadApiAsync(Player.AsPrimitive(),
                                                                  "surf_player_names",
                                                                  stageRecords: false,
                                                                  limit: 10,
                                                                  allowReadRepair: true);
        var stages = await storage.GetPlayerRecordsForReadApiAsync(Player.AsPrimitive(),
                                                                    "surf_player_names",
                                                                    stageRecords: true,
                                                                    limit: 10,
                                                                    allowReadRepair: true);

        Assert.Equal("Player One", Assert.Single(main).PlayerName);
        Assert.Equal("Player One", Assert.Single(stages).PlayerName);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ConcurrentReadersWaitForSeedCompletion(bool firstIsMap, bool secondIsMap)
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_wait");
        var best = await AddRun(storage, map.MapId, 80);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (!sql.Contains("row_number()", StringComparison.OrdinalIgnoreCase)) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Seed was not released");
        };

        Task Seed(bool mapWide)
        {
            var name = mapWide ? "EnsureBestRunsSeededForMapAsync" : "EnsureBestRunsSeededAsync";
            object[] arguments = mapWide ? [map.MapId, RunType.Main] : [map.MapId, RunType.Main, 0, (ushort)0, (ushort)0];
            return (Task)typeof(StorageServiceImpl).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!
                                                  .Invoke(storage, arguments)!;
        }

        var first = Task.Run(() => Seed(firstIsMap));
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            // Calling directly runs up to the first await; the completed map cache
            // must not let this reader skip the pending seed.
            second = Seed(secondIsMap);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (second is not null) await second.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal((long)best.Id, (await storage.GetPlayerRecord(Player, "surf_wait", 0, 0))!.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedSeedCanBeRetried(bool mapWide)
    {
        var storage = CreateStorage();
        var map = await storage.GetMapInfo("surf_retry");
        var best = await AddRun(storage, map.MapId, 80);
        storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("INSERT INTO `surf_player_best_runs`")) throw new InvalidOperationException("Injected seed write failure");
        };

        async Task<long> Read()
            => mapWide ? Assert.Single(await storage.GetPlayerRecords(Player, "surf_retry")).Id
                       : (await storage.GetPlayerRecord(Player, "surf_retry", 0, 0))!.Id;

        await Assert.ThrowsAnyAsync<Exception>(() => Read());
        storage.Db.Aop.OnLogExecuting = null;
        Assert.Equal((long)best.Id, await Read());
    }

    public void Dispose()
    {
        _storage?.Shutdown();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // SQLite can retain an outstanding file handle briefly when this concurrent
            // seed test runs beside the rest of the suite. The assertions and storage
            // shutdown already completed; temporary-file cleanup is best effort.
        }
    }
}
