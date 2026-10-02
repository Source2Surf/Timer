using System;
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
