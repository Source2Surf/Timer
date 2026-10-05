using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Models;
using Xunit;

namespace Timer.Tests;

public sealed class OtherMapRecordsTests
{
    private readonly List<(string Map, TaskCompletionSource<(IReadOnlyList<RunRecord>, IReadOnlyList<RunRecord>)> Source)> _fetches = [];
    private readonly SemaphoreSlim _landed = new(0);
    private long _now = 1_000_000;

    private OtherMapRecords Create()
        => new(map =>
               {
                   var source = new TaskCompletionSource<(IReadOnlyList<RunRecord>, IReadOnlyList<RunRecord>)>();
                   _fetches.Add((map, source));

                   return source.Task;
               },
               action =>
               {
                   action();
                   _landed.Release();

                   return Task.CompletedTask;
               },
               () => _now,
               _ => { },
               NullLogger.Instance);

    // Returns once the load has reached the game thread.
    private async Task Complete(int fetch, params RunRecord[][] boards)
    {
        _fetches[fetch].Source.SetResult((boards[0], boards.Length > 1 ? boards[1] : []));
        Assert.True(await _landed.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private async Task Fail(int fetch)
    {
        _fetches[fetch].Source.SetException(new InvalidOperationException("backend down"));
        Assert.True(await _landed.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static RunRecord Run(long id, float time, int track = 0, int stage = 0)
        => new() { Id = id, Time = time, Track = track, Stage = stage };

    [Fact]
    public async Task LoadsOnFirstReadThenServesSortedBoards()
    {
        var cache = Create();
        Assert.Null(cache.GetRecords("surf_a", 0, 0, 0));
        Assert.Null(cache.GetBoards("surf_a"));
        Assert.Single(_fetches);

        RunRecord fast = Run(1, 80), slow = Run(2, 90), bonus = Run(3, 50, track: 1), stage = Run(4, 10, stage: 2);
        await Complete(0, [slow, fast, bonus], [stage]);

        Assert.Equal(1, cache.Version);
        Assert.Equal([fast, slow], cache.GetRecords("SURF_A", 0, 0, 0)!);
        Assert.Equal([stage], cache.GetRecords("surf_a", 0, 0, 2)!);
        Assert.Empty(cache.GetRecords("surf_a", 1, 0, 0)!);
        Assert.Equal([(0, 0, 0), (0, 0, 2), (0, 1, 0)], cache.GetBoards("surf_a")!);
        Assert.Single(_fetches);
    }

    [Fact]
    public async Task AStaleMapRefreshesInTheBackgroundAndAFailedLoadWaitsToRetry()
    {
        var cache = Create();
        cache.GetRecords("surf_a", 0, 0, 0);
        var first = Run(1, 80);
        await Complete(0, [first]);

        _now += OtherMapRecords.StaleMs;
        Assert.Equal([first], cache.GetRecords("surf_a", 0, 0, 0)!);
        Assert.Equal(2, _fetches.Count);
        await Fail(1);

        Assert.Equal([first], cache.GetRecords("surf_a", 0, 0, 0)!);
        Assert.Equal(2, _fetches.Count);
        _now += OtherMapRecords.RetryMs;
        cache.GetRecords("surf_a", 0, 0, 0);
        Assert.Equal(3, _fetches.Count);

        // A map that never loaded stays loading until its retry.
        cache.GetRecords("surf_b", 0, 0, 0);
        await Fail(3);
        Assert.Null(cache.GetRecords("surf_b", 0, 0, 0));
        Assert.Equal(4, _fetches.Count);
        _now += OtherMapRecords.RetryMs;
        Assert.Null(cache.GetRecords("surf_b", 0, 0, 0));
        Assert.Equal(5, _fetches.Count);
    }

    [Fact]
    public async Task KeepsTheMostRecentlyUsedMaps()
    {
        var cache = Create();

        for (var map = 0; map < OtherMapRecords.MaxMaps; map++)
        {
            _now++;
            cache.GetRecords($"surf_{map}", 0, 0, 0);
            await Complete(_fetches.Count - 1, [Run(map + 1, 60)]);
        }

        _now++;
        cache.GetRecords("surf_0", 0, 0, 0);
        _now++;
        Assert.Null(cache.GetRecords("surf_new", 0, 0, 0));

        Assert.NotNull(cache.GetRecords("surf_0", 0, 0, 0));
        Assert.Equal(OtherMapRecords.MaxMaps + 1, _fetches.Count);
        Assert.Null(cache.GetRecords("surf_1", 0, 0, 0));
        Assert.Equal("surf_1", _fetches[^1].Map);
    }

    [Fact]
    public async Task ADeletedRunLeavesAtOnceAndALoadFromBeforeCannotBringItBack()
    {
        var cache = Create();
        cache.GetRecords("surf_a", 0, 0, 0);
        RunRecord deleted = Run(1, 80), other = Run(2, 90), next = Run(5, 95);
        await Complete(0, [deleted, other]);

        _now += OtherMapRecords.StaleMs;
        cache.GetRecords("surf_a", 0, 0, 0);
        var before = cache.Version;

        cache.OnRunDeleted("SURF_A", deleted.Id);
        Assert.True(cache.Version > before);
        Assert.Equal([other], cache.GetRecords("surf_a", 0, 0, 0)!);
        Assert.Equal(3, _fetches.Count);

        await Complete(1, [deleted, other]);
        Assert.Equal([other], cache.GetRecords("surf_a", 0, 0, 0)!);

        await Complete(2, [other, next]);
        Assert.Equal([other, next], cache.GetRecords("surf_a", 0, 0, 0)!);
        Assert.Equal(3, _fetches.Count);

        // A map nobody viewed has nothing to update.
        cache.OnRunDeleted("surf_unseen", 7);
        Assert.Equal(3, _fetches.Count);
    }

    [Fact]
    public void TheCurrentMapListsItsBoardsWithRecords()
    {
        var cache = new MapRecordCache(NullLogger.Instance);
        cache.Populate([Run(1, 80), new RunRecord { Id = 2, Time = 50, Style = 1, Track = 2 }],
                       [new RunRecord { Id = 3, Time = 10, Stage = 3 }],
                       cache.BeginLoad());

        Assert.Equal([(0, 0, 0), (0, 0, 3), (1, 2, 0)], cache.GetBoards());

        var version = cache.Version;
        cache.RefreshTrack(0, 0, [], cache.BeginLoad());
        Assert.NotEqual(version, cache.Version);
        Assert.Equal([(0, 0, 3), (1, 2, 0)], cache.GetBoards());
    }
}
