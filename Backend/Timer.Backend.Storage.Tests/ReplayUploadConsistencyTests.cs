using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

public sealed class ReplayUploadConsistencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-replay-upload-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;
    private const ulong SteamId = 76561198000000001;
    private const ulong RunId = 42;
    private const string MapName = "surf_upload";

    public ReplayUploadConsistencyTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
            NullLogger<StorageServiceImpl>.Instance, false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Db.CodeFirst.InitTables(typeof(MapEntity), typeof(MapTrackEntity), typeof(RunEntity), typeof(ReplayEntity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetriedUploadPointsTheRunAtTheNewObject(bool stage)
    {
        await SeedRunAsync(stage);
        Assert.True(await _storage.SaveReplayUrlAsync(MapName, SteamId, RunId, "https://test.invalid/first"));
        Assert.True(await _storage.SaveReplayUrlAsync(MapName, SteamId, RunId, "https://test.invalid/second"));

        Assert.Equal("https://test.invalid/second", await GetUrlAsync(stage));
        Assert.Equal(1, await _storage.Db.Queryable<ReplayEntity>().CountAsync());
    }

    [Fact]
    public async Task MissingRunIsNotPointedAtAnUpload()
    {
        await SeedRunAsync(false);
        await _storage.Db.Deleteable<RunEntity>().Where(x => x.Id == RunId).ExecuteCommandAsync();

        Assert.False(await _storage.SaveReplayUrlAsync(MapName, SteamId, RunId, "https://test.invalid/removed"));
        Assert.Equal(0, await _storage.Db.Queryable<ReplayEntity>().CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EveryPersonalBestReplayIsKept(bool stage) => PersonalBestsAreKept(_storage, stage);

    [DisposableDatabaseFact("TIMER_TEST_MYSQL")]
    public Task EveryPersonalBestReplayIsKeptOnMySql() => PersonalBestsAreKeptOn(DbType.MySql, "TIMER_TEST_MYSQL");

    [DisposableDatabaseFact("TIMER_TEST_POSTGRES")]
    public Task EveryPersonalBestReplayIsKeptOnPostgreSql() => PersonalBestsAreKeptOn(DbType.PostgreSQL, "TIMER_TEST_POSTGRES");

    private static async Task PersonalBestsAreKeptOn(DbType type, string variable)
    {
        var storage = new StorageServiceImpl(type, Environment.GetEnvironmentVariable(variable)!,
            NullLogger<StorageServiceImpl>.Instance, false);
        try
        {
            storage.Db.DbMaintenance.CreateDatabase();
            storage.Init(initializeSchema: true, startScoreRecalcWorker: false);
            await PersonalBestsAreKept(storage, false);
            await PersonalBestsAreKept(storage, true);
        }
        finally { storage.Shutdown(); }
    }

    // A PB stays when the player beats it; the fastest is the one served. Slower runs are never uploaded.
    private static async Task PersonalBestsAreKept(StorageServiceImpl storage, bool stage)
    {
        var mapName = $"surf_test_{Guid.NewGuid():N}";
        var map = await storage.GetMapInfo(mapName);
        var player = 76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1000000000);
        var other = player + 1;

        async Task<ulong> Seed(bool s, float time, ulong steamId)
            => (ulong)await storage.Db.Insertable(new RunEntity
            {
                MapId = map.MapId, SteamId = (long)steamId,
                RunType = s ? RunType.Stage : RunType.Main, Stage = s ? (ushort)1 : (ushort)0, Time = time,
                DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
            }).ExecuteReturnBigIdentityAsync();

        Task Upload(ulong runId, ulong steamId)
            => storage.SaveReplayUrlAsync(mapName, steamId, runId, $"https://test.invalid/{runId}");

        var slow = await Seed(stage, 80, player);
        var fast = await Seed(stage, 70, player);
        var late = await Seed(stage, 75, player);
        var others = await Seed(stage, 60, other);
        var otherLeaderboard = await Seed(!stage, 50, player);

        await Upload(slow, player);
        await Upload(otherLeaderboard, player);
        await Upload(others, other);
        await Upload(fast, player);

        var kept = await storage.Db.Queryable<ReplayEntity>().Where(x => x.MapId == map.MapId).OrderBy(x => x.RunId).ToListAsync();
        Assert.Equal(new[] { slow, fast, others, otherLeaderboard }.Order(), kept.Select(x => x.RunId));
        Assert.Equal(new[] { slow, fast, others, otherLeaderboard }.Order(),
                     (await storage.GetStoredReplayRunIdsAsync([slow, fast, late, others, otherLeaderboard])).Order());
        var served = await storage.GetReplayUrlAsync(mapName, stage ? RunType.Stage : RunType.Main, 0, 0, stage ? 1 : 0, player);
        Assert.Equal($"https://test.invalid/{fast}", served);
        Assert.Equal($"https://test.invalid/{slow}", await storage.GetRunReplayUrlAsync(slow));
    }

    private Task<string?> GetUrlAsync(bool stage)
        => _storage.GetReplayUrlAsync(MapName, stage ? RunType.Stage : RunType.Main, 0, 0, stage ? 1 : 0, null);

    private async Task SeedRunAsync(bool stage)
    {
        var map = await _storage.GetMapInfo(MapName);
        await _storage.Db.Insertable(new RunEntity
        {
            Id = RunId, MapId = map.MapId, SteamId = (long)SteamId,
            RunType = stage ? RunType.Stage : RunType.Main, Stage = stage ? (ushort)1 : (ushort)0, Time = 80,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).OffIdentity().ExecuteCommandAsync();
    }

    public void Dispose()
    {
        _storage.Shutdown();
        File.Delete(_path);
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
