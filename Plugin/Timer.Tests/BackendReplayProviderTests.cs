using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

public sealed class BackendReplayProviderTests
{
    private const ulong  SteamId = 76561198000000001;
    private const ulong  RunId   = 42;
    private const string MapName = "surf_upload";

    private readonly Catalog               _catalog = new();
    private readonly ObjectStorage         _objects = new();
    private readonly BackendReplayProvider _provider;

    public BackendReplayProviderTests()
        => _provider = new BackendReplayProvider(_catalog, _objects, NullLogger<BackendReplayProvider>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRetryDoesNotDeleteAPreviouslyCommittedReplay(bool stage)
    {
        var bytes = Encoding.UTF8.GetBytes("complete replay");
        await UploadAsync(stage, bytes);
        var committed = _catalog.Urls[RunId];

        _catalog.FailSaves = true;
        await Assert.ThrowsAnyAsync<Exception>(() => UploadAsync(stage, bytes));

        Assert.Equal(2, _objects.Count); // The backend may have committed before its reply was lost.
        Assert.True(_objects.Contains(committed), "A failed retry deleted the successful upload's object.");
        Assert.Equal(bytes, await DownloadAsync(stage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedRetryUploadDoesNotTruncateAPreviouslyCommittedReplay(bool stage)
    {
        var bytes = Encoding.UTF8.GetBytes("complete replay");
        await UploadAsync(stage, bytes);
        _objects.FailNextUploadAfterTruncation = true;

        await Assert.ThrowsAsync<IOException>(() => UploadAsync(stage, bytes));

        Assert.Equal(bytes, await DownloadAsync(stage));
    }

    [Fact]
    public async Task DefinitelyMissingRunCleansUpOnlyTheUnusedAttempt()
    {
        _catalog.RunExists = false;

        await UploadAsync(false, Encoding.UTF8.GetBytes("already removed"));

        Assert.Equal(0, _objects.Count);
        Assert.Empty(_catalog.Urls);
    }

    [Fact]
    public async Task AnUnreachableReplayIsServedAsNone()
    {
        _catalog.Urls[RunId] = "https://test.invalid/gone";

        Assert.Null(await _provider.GetRunReplayAsync(RunId));
    }

    [Fact]
    public async Task AStreamIsUploadedFromItsPositionToItsEnd()
    {
        using var data = new MemoryStream([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        data.Position = 3;

        await _provider.UploadReplayAsync(MapName, 0, 0, SteamId, RunId, data);

        Assert.Equal([3, 4, 5, 6, 7, 8, 9], _objects.Get(_catalog.Urls[RunId]));
    }

    // A replay file goes to the built-in provider as it is, to be sent as it's read; an external provider, whose
    // contract takes a byte array, gets the file read in.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task TheBuiltInProviderIsHandedTheFileAndAnExternalOneItsBytes(int stage)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.replay");
        byte[] contents = [.. Enumerable.Range(0, 300_000).Select(i => (byte) i)];
        await File.WriteAllBytesAsync(path, contents);

        try
        {
            await using (var file = ReplayShared.OpenForUpload(path))
            {
                await ReplayProviderProxy.UploadAsync(_provider, MapName, 0, 0, stage, SteamId, RunId, file);

                Assert.Same(file, _objects.LastData);
                Assert.Equal(contents, _objects.Get(_catalog.Urls[RunId]));
            }

            var external = new ExternalProvider();

            await using (var file = ReplayShared.OpenForUpload(path))
            {
                file.Position = 100;
                await ReplayProviderProxy.UploadAsync(external, MapName, 0, 0, stage, SteamId, RunId, file);
            }

            var upload = Assert.Single(external.Uploads);
            Assert.Equal(stage, upload.Stage);
            Assert.Equal(contents[100..], upload.Data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private Task UploadAsync(bool stage, byte[] bytes)
        => stage ? _provider.UploadStageReplayAsync(MapName, 0, 0, 1, SteamId, RunId, bytes)
                 : _provider.UploadReplayAsync(MapName, 0, 0, SteamId, RunId, bytes);

    // Another module's IReplayProvider: only the contract's byte array uploads.
    private sealed class ExternalProvider : IReplayProvider
    {
        public List<(int Stage, byte[] Data)> Uploads { get; } = [];

        public Task<byte[]?> GetReplayAsync(string mapName, int style, int track, ulong? steamId = null)
            => Task.FromResult<byte[]?>(null);

        public Task<byte[]?> GetStageReplayAsync(string mapName, int style, int track, int stage, ulong? steamId = null)
            => Task.FromResult<byte[]?>(null);

        public Task UploadReplayAsync(string mapName, int style, int track, ulong steamId, ulong runId, byte[] replayData)
        {
            Uploads.Add((0, replayData));
            return Task.CompletedTask;
        }

        public Task UploadStageReplayAsync(string mapName, int style, int track, int stage, ulong steamId, ulong runId, byte[] replayData)
        {
            Uploads.Add((stage, replayData));
            return Task.CompletedTask;
        }
    }

    private Task<byte[]?> DownloadAsync(bool stage)
        => stage ? _provider.GetStageReplayAsync(MapName, 0, 0, 1)
                 : _provider.GetReplayAsync(MapName, 0, 0);

    // One run on one board, as far as these tests go.
    private sealed class Catalog : IReplayCatalog
    {
        public Dictionary<ulong, string> Urls      { get; } = [];
        public bool                      FailSaves { get; set; }
        public bool                      RunExists { get; set; } = true;

        public Task<string?> GetReplayUrlAsync(string mapName, bool stageRun, int style, int track, int stage, ulong? steamId)
            => Task.FromResult(Urls.GetValueOrDefault(RunId));

        public Task<string?> GetRunReplayUrlAsync(ulong runId)
            => Task.FromResult(Urls.GetValueOrDefault(runId));

        public Task<IReadOnlyCollection<ulong>> GetStoredReplayRunIdsAsync(IReadOnlyList<ulong> runIds)
            => Task.FromResult<IReadOnlyCollection<ulong>>(runIds.Where(Urls.ContainsKey).ToList());

        public Task<bool> SaveReplayUrlAsync(string mapName, ulong steamId, ulong runId, string url)
        {
            if (FailSaves)
            {
                throw new InvalidOperationException("Injected backend failure");
            }

            if (!RunExists)
            {
                return Task.FromResult(false);
            }

            Urls[runId] = url;

            return Task.FromResult(true);
        }
    }

    private sealed class ObjectStorage : IReplayStorage
    {
        private readonly Dictionary<string, byte[]> _objects = new();
        public int Count => _objects.Count;
        public bool FailNextUploadAfterTruncation { get; set; }
        public bool Contains(string url) => _objects.ContainsKey(url);
        public byte[] Get(string url) => _objects[url];

        // The stream the last upload was read from.
        public Stream? LastData { get; private set; }

        public async Task<string> UploadAsync(string key, Stream data)
        {
            LastData = data;
            var url = $"https://test.invalid/{key}";
            // Match the included HTTP storage server's File.Create before copying the body.
            _objects[url] = [];
            if (FailNextUploadAfterTruncation)
            {
                FailNextUploadAfterTruncation = false;
                throw new IOException("Interrupted upload after object truncation.");
            }
            using var body = new MemoryStream();
            await data.CopyToAsync(body);
            _objects[url] = body.ToArray();
            return url;
        }

        public Task<byte[]> DownloadAsync(string url)
            => Task.FromResult(_objects.TryGetValue(url, out var bytes) ? bytes : throw new FileNotFoundException(url));

        public Task DeleteAsync(string url)
        {
            _objects.Remove(url);
            return Task.CompletedTask;
        }
    }
}
