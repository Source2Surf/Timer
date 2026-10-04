using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.RequestManager.Replay;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class ReplayUploadConsistencyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-replay-upload-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;
    private readonly ObjectStorage _objects = new();
    private readonly DbReplayProvider _provider;
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
        _provider = new DbReplayProvider(_storage, _objects, NullLogger<DbReplayProvider>.Instance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRetryDoesNotDeleteAPreviouslyCommittedReplay(bool stage)
    {
        await SeedRunAsync(stage);
        var bytes = Encoding.UTF8.GetBytes("complete replay");
        await UploadAsync(stage, bytes);
        var committed = (await _storage.Db.Queryable<ReplayEntity>().SingleAsync()).Replay;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (sql.Contains("surf_runs_replay", StringComparison.OrdinalIgnoreCase)
                && (sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                    || sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Injected metadata failure");
        };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(
                () => UploadAsync(stage, bytes));
        }
        finally { _storage.Db.Aop.OnLogExecuting = null; }

        Assert.Equal(2, _objects.Count); // SQL exceptions can leave the commit outcome unknown.
        Assert.True(_objects.Contains(committed), "A failed retry deleted the successful upload's object.");
        Assert.Equal(bytes, await DownloadAsync(stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRetryUploadDoesNotTruncateAPreviouslyCommittedReplay(bool stage)
    {
        await SeedRunAsync(stage);
        var bytes = Encoding.UTF8.GetBytes("complete replay");
        await UploadAsync(stage, bytes);
        _objects.FailNextUploadAfterTruncation = true;
        await Assert.ThrowsAsync<IOException>(
            () => UploadAsync(stage, bytes));

        Assert.Equal(bytes, await DownloadAsync(stage));
    }

    [Fact]
    public async Task DefinitelyMissingRunCleansUpOnlyTheUnusedAttempt()
    {
        await SeedRunAsync(false);
        await _storage.Db.Deleteable<RunEntity>().Where(x => x.Id == RunId).ExecuteCommandAsync();
        await UploadAsync(false, Encoding.UTF8.GetBytes("already removed"));
        Assert.Equal(0, _objects.Count);
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
        var objects = new ObjectStorage();
        var provider = new DbReplayProvider(storage, objects, NullLogger<DbReplayProvider>.Instance);
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

        Task Upload(bool s, ulong runId, ulong steamId)
            => s ? provider.UploadStageReplayAsync(mapName, 0, 0, 1, steamId, runId, [(byte)runId])
                 : provider.UploadReplayAsync(mapName, 0, 0, steamId, runId, [(byte)runId]);

        var slow = await Seed(stage, 80, player);
        var fast = await Seed(stage, 70, player);
        var late = await Seed(stage, 75, player);
        var others = await Seed(stage, 60, other);
        var otherLeaderboard = await Seed(!stage, 50, player);

        await Upload(stage, slow, player);
        await Upload(!stage, otherLeaderboard, player);
        await Upload(stage, others, other);
        await Upload(stage, fast, player);

        var kept = await storage.Db.Queryable<ReplayEntity>().Where(x => x.MapId == map.MapId).OrderBy(x => x.RunId).ToListAsync();
        Assert.Equal(new[] { slow, fast, others, otherLeaderboard }.Order(), kept.Select(x => x.RunId));
        Assert.Equal(4, objects.Count);
        Assert.All(kept, x => Assert.True(objects.Contains(x.Replay)));
        Assert.Equal(new[] { slow, fast, others, otherLeaderboard }.Order(),
                     (await provider.GetStoredRunIdsAsync([slow, fast, late, others, otherLeaderboard])).Order());
        var served = stage ? await provider.GetStageReplayAsync(mapName, 0, 0, 1, player)
                           : await provider.GetReplayAsync(mapName, 0, 0, player);
        Assert.NotNull(served);
        Assert.Equal([(byte)fast], served);
    }

    private Task UploadAsync(bool stage, byte[] bytes)
        => stage ? _provider.UploadStageReplayAsync(MapName, 0, 0, 1, SteamId, RunId, bytes)
                 : _provider.UploadReplayAsync(MapName, 0, 0, SteamId, RunId, bytes);

    private Task<byte[]?> DownloadAsync(bool stage)
        => stage ? _provider.GetStageReplayAsync(MapName, 0, 0, 1)
                 : _provider.GetReplayAsync(MapName, 0, 0);

    private Task SeedRunAsync(bool stage) => SeedRunAsync(stage, RunId, 80);

    private async Task SeedRunAsync(bool stage, ulong runId, float time)
    {
        var map = await _storage.GetMapInfo(MapName);
        await _storage.Db.Insertable(new RunEntity
        {
            Id = runId, MapId = map.MapId, SteamId = (long)SteamId,
            RunType = stage ? RunType.Stage : RunType.Main, Stage = stage ? (ushort)1 : (ushort)0, Time = time,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).OffIdentity().ExecuteCommandAsync();
    }

    private sealed class ObjectStorage : IReplayStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();
        public int Count => _objects.Count;
        public bool FailNextUploadAfterTruncation { get; set; }
        public bool Contains(string url) => _objects.ContainsKey(url);

        public Task<string> UploadAsync(string key, byte[] data)
        {
            var url = $"https://test.invalid/{key}";
            // Match the included HTTP storage server's File.Create before copying the body.
            _objects[url] = [];
            if (FailNextUploadAfterTruncation)
            {
                FailNextUploadAfterTruncation = false;
                throw new IOException("Interrupted upload after object truncation.");
            }
            _objects[url] = data.ToArray();
            return Task.FromResult(url);
        }

        public Task<byte[]> DownloadAsync(string url)
            => Task.FromResult(_objects.TryGetValue(url, out var bytes) ? bytes : throw new FileNotFoundException(url));

        public Task DeleteAsync(string url)
        {
            _objects.Remove(url);
            return Task.CompletedTask;
        }
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
