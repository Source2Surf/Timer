using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Modules.Replay;
using Xunit;

namespace Timer.Tests;

// Slower runs' replays, kept for the replay menu's My runs: the newest few per player and leaderboard.
public sealed class ReplayRecentRunTests : IDisposable
{
    private const string Map     = "surf_recent";
    private const ulong  Player  = 76561198000000001;
    private const ulong  Another = 76561198000000002;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"timer-recent-{Guid.NewGuid():N}");

    [Fact]
    public void KeepsTheNewestRunsOfEachPlayerAndLeaderboard()
    {
        foreach (var runId in new long[] { 3, 1, 4, 2 })
        {
            Keep(Player, stage: 0, runId, keep: 2);
        }

        Keep(Player, stage: 2, 5, keep: 2);
        Keep(Another, stage: 0, 6, keep: 2);

        Assert.Equal([3L, 4L], Kept(Player, stage: 0));
        Assert.Equal([5L], Kept(Player, stage: 2));
        Assert.Equal([6L], Kept(Another, stage: 0));
    }

    [Fact]
    public void KeepingNoneDeletesTheReplay()
    {
        var path = Keep(Player, stage: 0, 7, keep: 0);

        Assert.False(File.Exists(path));
        Assert.Empty(Kept(Player, stage: 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void PersonalBestsStayWhateverTheirAge(int stage)
    {
        var best = ReplayShared.BuildReplayPath(_directory, Map, 0, 0, stage, 3);
        Directory.CreateDirectory(Path.GetDirectoryName(best)!);
        File.WriteAllText(best, "3");
        Age(best, DateTime.UtcNow.AddDays(-100));

        Assert.Equal(0, ReplayShared.DeleteOldRecentRuns(_directory, DateTime.UtcNow.AddDays(-3), NullLogger.Instance));
        Assert.True(File.Exists(best));
    }

    [Fact]
    public void OnlySlowerRunsOlderThanTheCutoffAreDeleted()
    {
        var now = DateTime.UtcNow;
        Keep(Player, stage: 0, 1, keep: 10);
        Keep(Player, stage: 0, 2, keep: 10);
        Keep(Player, stage: 2, 3, keep: 10);
        Keep(Another, stage: 0, 4, keep: 10);
        Age(BuildRecent(Player, 0, 1), now.AddDays(-40));
        Age(BuildRecent(Player, 2, 3), now.AddDays(-40));
        Age(BuildRecent(Another, 0, 4), now.AddDays(-40));
        var best = ReplayShared.BuildReplayPath(_directory, Map, 0, 0, 0, 5);
        File.WriteAllText(best, "5");
        Age(best, now.AddDays(-400));

        var stageFolder = Path.GetDirectoryName(BuildRecent(Player, 2, 3))!;
        Assert.Equal(3, ReplayShared.DeleteOldRecentRuns(_directory, now.AddDays(-30), NullLogger.Instance));
        Assert.Equal([2L], Kept(Player, stage: 0));
        Assert.True(File.Exists(best));
        // Just emptied, so a run being moved in right now still finds it.
        Assert.True(Directory.Exists(stageFolder));

        // An hour later the whole emptied chain goes in one sweep.
        foreach (var folder in FoldersUnder(Path.Combine(_directory, "style_0", "recent")))
        {
            Directory.SetLastWriteTimeUtc(folder, now.AddHours(-2));
        }
        ReplayShared.DeleteOldRecentRuns(_directory, now.AddDays(-30), NullLogger.Instance);
        Assert.False(Directory.Exists(stageFolder));
        Assert.False(Directory.Exists(Path.Combine(_directory, "style_0", "recent", Another.ToString())));
        Assert.Equal([2L], Kept(Player, stage: 0));
    }

    private string BuildRecent(ulong steamId, int stage, long runId)
        => ReplayShared.BuildRecentRunPath(_directory, Map, 0, 0, stage, steamId, runId);

    private static void Age(string path, DateTime writeTimeUtc) => File.SetLastWriteTimeUtc(path, writeTimeUtc);

    private static IEnumerable<string> FoldersUnder(string folder)
        => Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).ToArray();

    // Writes a finished run's file as the recorder does, then hands it over like a slower run.
    private string Keep(ulong steamId, int stage, long runId, int keep)
    {
        var path = ReplayShared.BuildReplayPath(_directory, Map, 0, 0, stage, runId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, runId.ToString());

        ReplayShared.KeepRecentRun(path, _directory, Map, 0, 0, stage, steamId, runId, keep, NullLogger.Instance);

        return path;
    }

    private long[] Kept(ulong steamId, int stage)
    {
        var directory = Path.GetDirectoryName(ReplayShared.BuildRecentRunPath(_directory, Map, 0, 0, stage, steamId, 0))!;

        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Select(f => long.Parse(Path.GetFileNameWithoutExtension(f))).Order().ToArray()
            : [];
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }
}
