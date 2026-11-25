using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

// A finished run's replay takes the recording's chunks; the recording keeps a copy of its end for the next run.
public sealed class ReplayBufferTests
{
    private static PlayerFrameData Recording(int frames)
    {
        var data = new PlayerFrameData { Name = "Nuko" };

        for (var i = 0; i < frames; i++)
        {
            data.Frames.Add(new ReplayFrameData { Origin = new Vector(i, 0, 0) });
        }

        return data;
    }

    [Fact]
    public void AFinishedRunGoesToItsSnapshotWithoutACopy()
    {
        var data   = Recording(3000);
        var chunks = data.Frames.ChunkCount;
        data.TimerStartFrame  = 100;
        data.TimerFinishFrame = 2900;

        var snapshot = ReplayShared.CreateMainReplaySnapshot(data, 0);

        var frames = Assert.IsType<ReplayFrameBuffer>(snapshot.Frames);
        Assert.Equal(3000, frames.Count);
        Assert.Equal(chunks, frames.ChunkCount);
        Assert.Equal(2999f, frames[^1].Origin.X);
        Assert.Equal((3000, 100, 2900), (snapshot.Header.TotalFrames, snapshot.Header.PreFrame, snapshot.Header.PostFrame));

        Assert.NotSame(frames, data.Frames);
        Assert.Empty(data.Frames);
        Assert.Equal(0, data.Frames.ChunkCount);
        Assert.Equal(0, data.TimerStartFrame);
    }

    [Fact]
    public void TheEndOfTheRecordingStaysAsTheNextRunsPreRun()
    {
        var data = Recording(3000);

        var snapshot = ReplayShared.CreateMainReplaySnapshot(data, 200);

        Assert.Equal(3000, snapshot.Frames.Count);
        Assert.Equal(200, data.Frames.Count);
        Assert.Equal((2800f, 2999f), (data.Frames[0].Origin.X, data.Frames[^1].Origin.X));

        ReplayShared.DiscardMainRecording(data, 50);

        Assert.Equal(50, data.Frames.Count);
        Assert.Equal(2950f, data.Frames[0].Origin.X);
    }

    [Fact]
    public void TheNextRunNeverReachesTheSnapshot()
    {
        var data     = Recording(3000);
        var snapshot = ReplayShared.CreateMainReplaySnapshot(data, 200);

        // The next run records, trims its pre-run and is dropped, all in chunks of its own.
        for (var i = 0; i < 2000; i++)
        {
            data.Frames.Add(new ReplayFrameData { Origin = new Vector(-1, 0, 0) });
        }

        ReplayShared.TrimPreRunFrames(data, 10);
        data.Frames.Clear();

        for (var i = 0; i < 3000; i++)
        {
            data.Frames.Add(new ReplayFrameData { Origin = new Vector(-2, 0, 0) });
        }

        Assert.Equal(3000, snapshot.Frames.Count);
        Assert.Equal((0f, 1500f, 2999f), (snapshot.Frames[0].Origin.X, snapshot.Frames[1500].Origin.X, snapshot.Frames[2999].Origin.X));
    }

    [Fact]
    public void AStageSnapshotIsACopyOfItsRange()
    {
        var data = Recording(3000);

        var snapshot = ReplayShared.CreateStageReplaySnapshot(data,
                                                              startTick: 500,
                                                              stageStartFrame: 600,
                                                              stageFinishFrame: 2000,
                                                              postRunFrameCount: 100,
                                                              finishTime: 20f);

        // The run goes on: it loses its front and rewinds past the stage.
        data.Frames.RemoveFirst(700);
        data.Frames.Truncate(10);

        Assert.IsType<ReplayFrameBuffer>(snapshot.Frames);
        Assert.Equal(1600, snapshot.Frames.Count);
        Assert.Equal((500f, 2099f), (snapshot.Frames[0].Origin.X, snapshot.Frames[^1].Origin.X));
        Assert.Equal((1600, 100, 1500), (snapshot.Header.TotalFrames, snapshot.Header.PreFrame, snapshot.Header.PostFrame));
    }

    [Fact]
    public void AStageRangeOutsideTheRecordingIsEmpty()
    {
        var data = Recording(100);

        var snapshot = ReplayShared.CreateStageReplaySnapshot(data,
                                                              startTick: 200,
                                                              stageStartFrame: 200,
                                                              stageFinishFrame: 150,
                                                              postRunFrameCount: 0,
                                                              finishTime: 1f);

        Assert.Empty(snapshot.Frames);
        Assert.Equal(0, snapshot.Header.TotalFrames);
    }

    [Fact]
    public void ReleasingASnapshotGivesItsChunksBack()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var frames = new ReplayFrameBuffer(pool);

        for (var i = 0; i < 2500; i++)
        {
            frames.Add(new ReplayFrameData());
        }

        var snapshot = new ReplaySaveSnapshot(new ReplayFileHeader(), frames);

        snapshot.ReleaseFrames();

        Assert.Empty(snapshot.Frames);
        Assert.Equal(3, pool.SpareCount);

        // Frames that aren't pooled are left as they are.
        var array = new ReplaySaveSnapshot(new ReplayFileHeader(), [new ReplayFrameData()]);
        array.ReleaseFrames();
        Assert.Single(array.Frames);
    }
}
