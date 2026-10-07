using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

// A finished run's replay is an exact copy; the player's recording buffer is kept for the next run.
public sealed class ReplayBufferTests
{
    private static PlayerFrameData Recording(int capacity, int frames)
    {
        var data = new PlayerFrameData { Name = "Nuko", Frames = new List<ReplayFrameData>(capacity) };

        for (var i = 0; i < frames; i++)
        {
            data.Frames.Add(new ReplayFrameData { Origin = new Vector(i, 0, 0) });
        }

        return data;
    }

    [Fact]
    public void AFinishedRunIsCopiedExactlyAndTheBufferIsKept()
    {
        var data   = Recording(4096, 3000);
        var buffer = data.Frames;
        data.TimerStartFrame  = 100;
        data.TimerFinishFrame = 2900;

        var snapshot = ReplayShared.CreateMainReplaySnapshot(data);

        var frames = Assert.IsType<ReplayFrameData[]>(snapshot.Frames);
        Assert.Equal(3000, frames.Length);
        Assert.Equal(2999f, frames[^1].Origin.X);
        Assert.Equal((3000, 100, 2900), (snapshot.Header.TotalFrames, snapshot.Header.PreFrame, snapshot.Header.PostFrame));

        Assert.Same(buffer, data.Frames);
        Assert.Empty(data.Frames);
        Assert.Equal(4096, data.Frames.Capacity);
        Assert.Equal(0, data.TimerStartFrame);
    }

    [Fact]
    public void ABufferFarBiggerThanTheRunShrinks()
    {
        var data = Recording(100_000, 2000);

        ReplayShared.CreateMainReplaySnapshot(data);

        Assert.Equal(2000, data.Frames.Capacity);
    }

    [Fact]
    public void AShortRunLeavesTheStartingSize()
    {
        var data = Recording(ReplayShared.InitialFrames, 100);

        ReplayShared.CreateMainReplaySnapshot(data);

        Assert.Equal(ReplayShared.InitialFrames, data.Frames.Capacity);
    }
}
