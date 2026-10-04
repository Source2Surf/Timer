using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;
using ZstdSharp;

namespace Timer.Tests;

// Best runs' replays on disk, deleted least recently used first once they are in remote storage.
public sealed class ReplayCacheTests : IDisposable
{
    private const string Map = "surf_cache_map";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"timer-cache-{Guid.NewGuid():N}");

    [Fact]
    public void ListsOnlyBestRunReplaysByTheirRunId()
    {
        Write(ReplayShared.BuildReplayPath(_directory, Map, 0, 0, 0, 11));
        Write(ReplayShared.BuildReplayPath(_directory, Map, 1, 2, 3, 12));
        Write(ReplayShared.BuildRecentRunPath(_directory, Map, 0, 0, 0, 76561198000000001, 13));
        Write(ReplayShared.BuildFallbackTempPath(_directory, Map, 0, 0, 0));
        Write(ReplayShared.BuildReplayPath(_directory, Map, 0, 0, 0, 14) + ".corrupt");

        Assert.Equal([11L, 12L], ReplayShared.ListCachedReplays(_directory).Select(c => c.RunId).Order());
    }

    [Fact]
    public async Task LoadingAReplayMarksItUsed()
    {
        var path = ReplayShared.BuildReplayPath(_directory, Map, 0, 0, 0, 21);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frames = new List<ReplayFrameData> { new() { Origin = new Vector(1, 2, 3) } };
        Assert.True(await ReplayShared.WriteReplayToFileAsync(new ReplayFileHeader { TotalFrames = 1 }, path, frames, 3, 0,
                                                              NullLogger.Instance));
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        Assert.True(Assert.Single(ReplayShared.ListCachedReplays(_directory)).LastUsedUtc < DateTime.UtcNow.AddDays(-9));

        using var decompressor = new Decompressor();
        Assert.NotNull(ReplayShared.LoadReplayFromPath(path, 0, 0, 0, decompressor, NullLogger.Instance));

        Assert.True(Assert.Single(ReplayShared.ListCachedReplays(_directory)).LastUsedUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void EvictsTheLeastRecentlyUsedStoredReplaysUntilWithinBudget()
    {
        var now = DateTime.UtcNow;
        ReplayShared.CachedReplay[] cached =
        [
            new("a", 1, 100, now.AddDays(-5)),
            new("b", 2, 100, now.AddDays(-9)), // not in remote storage
            new("c", 3, 100, now.AddDays(-4)),
            new("d", 4, 100, now.AddDays(-3)),
            new("e", 5, 100, now.AddMinutes(-5)), // just used
        ];
        var stored = new HashSet<long> { 1, 3, 4, 5 };

        Assert.Equal(["a", "c"], ReplayShared.ChooseEvictions(cached, 300, stored, now.AddHours(-1)).Select(c => c.Path));
        // Even when everything evictable goes, the rest stays over budget.
        Assert.Equal(["a", "c", "d"], ReplayShared.ChooseEvictions(cached, 0, stored, now.AddHours(-1)).Select(c => c.Path));
        Assert.Empty(ReplayShared.ChooseEvictions(cached, 500, stored, now.AddHours(-1)));
    }

    [Fact]
    public void ADownloadedReplayIsSavedWhereItsFileBelongsWithoutReplacingOne()
    {
        var path = ReplayShared.BuildReplayPath(_directory, Map, 0, 0, 2, 31);

        ReplayShared.CacheDownloadedReplay(path, [1, 2, 3], NullLogger.Instance);
        ReplayShared.CacheDownloadedReplay(path, [9], NullLogger.Instance);

        Assert.Equal([1, 2, 3], File.ReadAllBytes(path));
        Assert.Equal([path], Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
    }

    private static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "replay");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
