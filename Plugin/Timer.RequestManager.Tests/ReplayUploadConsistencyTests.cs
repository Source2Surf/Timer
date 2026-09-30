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
        _provider = new DbReplayProvider(_storage, _objects, false, NullLogger<DbReplayProvider>.Instance);
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

    private Task UploadAsync(bool stage, byte[] bytes)
        => stage ? _provider.UploadStageReplayAsync(MapName, 0, 0, 1, SteamId, RunId, bytes)
                 : _provider.UploadReplayAsync(MapName, 0, 0, SteamId, RunId, bytes);

    private Task<byte[]?> DownloadAsync(bool stage)
        => stage ? _provider.GetStageReplayAsync(MapName, 0, 0, 1)
                 : _provider.GetReplayAsync(MapName, 0, 0);

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
}
