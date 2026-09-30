using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Models;
using Xunit;

namespace Timer.Tests;

public sealed class RecordCacheTests
{
    private static readonly PlayerSlot Slot = new(1);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SlowerFinishAndLateLoadCannotReplacePersonalBest(int stage)
    {
        var cache = new PlayerRecordCache(NullLogger.Instance);
        var best = new RunRecord { Id = 20, Time = 80, Stage = stage };
        var slow = new RunRecord { Id = 30, Time = 120, Stage = stage };
        if (stage == 0)
        {
            cache.SetRecord(Slot, 0, 0, best);
            cache.SetRecord(Slot, 0, 0, slow);
            cache.Populate(Slot, [slow], []);
        }
        else
        {
            cache.SetStageRecord(Slot, 0, 0, stage, best);
            cache.SetStageRecord(Slot, 0, 0, stage, slow);
            cache.Populate(Slot, [], [slow]);
        }
        Assert.Same(best, cache.GetRecord(Slot, 0, 0, stage));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void DuplicateLoadChoosesTimeThenIdRegardlessOfInputOrder(int stage)
    {
        var cache = new PlayerRecordCache(NullLogger.Instance);
        var best = new RunRecord { Id = 10, Time = 80, Stage = stage };
        var tied = new RunRecord { Id = 20, Time = 80, Stage = stage };
        var slow = new RunRecord { Id = 30, Time = 120, Stage = stage };
        RunRecord[] records = [best, tied, slow];
        cache.Populate(Slot, stage == 0 ? records : [], stage > 0 ? records : []);
        Assert.Same(best, cache.GetRecord(Slot, 0, 0, stage));
    }

    [Fact]
    public void RecordSortUsesRunIdToBreakEqualTimes()
    {
        var earlier = new RunRecord { Id = 10, Time = 80 };
        var later = new RunRecord { Id = 20, Time = 80 };
        Assert.True(earlier.CompareTo(later) < 0);
    }

    [Fact]
    public void OlderMapLoadCannotReplaceNewerTrackOrCheckpoints()
    {
        var cache = new MapRecordCache(NullLogger.Instance);
        var oldLoad = cache.BeginLoad();
        var newLoad = cache.BeginLoad();
        var best = new RunRecord { Id = 20, Time = 80 };
        var slow = new RunRecord { Id = 10, Time = 120 };
        RunCheckpoint[] bestCheckpoints = [new() { RecordId = best.Id, Time = 40 }];
        cache.RefreshTrack(0, 0, [best], newLoad);
        cache.RefreshStage(0, 0, 1, [best], newLoad);
        cache.SetWRCheckpoints(0, 0, bestCheckpoints, newLoad);

        cache.Populate([slow], [], oldLoad);
        cache.RefreshTrack(0, 0, [slow], oldLoad);
        cache.RefreshStage(0, 0, 1, [slow], oldLoad);
        cache.SetWRCheckpoints(0, 0, [], oldLoad);

        Assert.Same(best, cache.GetWR(0, 0));
        Assert.Same(best, cache.GetWR(0, 0, 1));
        Assert.Same(bestCheckpoints, cache.GetWRCheckpoints(0, 0));
        Assert.Single(cache.GetRecords(0, 0));
    }

    [Fact]
    public void MapChangeRejectsOldLoadsEvenIfTheirRefreshStartsAfterClear()
    {
        var cache = new MapRecordCache(NullLogger.Instance);
        var oldLoad = cache.BeginLoad();
        cache.Clear();
        var lateRefresh = cache.BeginLoad(oldLoad);
        var currentLoad = cache.BeginLoad();
        var current = new RunRecord { Id = 30, Time = 180 };
        var old = new RunRecord { Id = 10, Time = 20 };
        cache.RefreshTrack(0, 0, [current], currentLoad);
        cache.Populate([old], [], oldLoad);
        cache.RefreshTrack(0, 0, [old], lateRefresh);
        cache.RefreshStage(0, 0, 1, [old], lateRefresh);
        Assert.False(cache.IsCurrent(oldLoad));
        Assert.Same(current, cache.GetWR(0, 0));
        Assert.Null(cache.GetWR(0, 0, 1));
    }
}
