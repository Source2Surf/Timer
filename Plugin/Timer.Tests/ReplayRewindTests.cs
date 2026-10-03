using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

public sealed class ReplayRewindTests
{
    [Fact]
    public void RewindDropsLaterFramesAndRestoresStageTicks()
    {
        var frameData = Recording(60);
        frameData.NewStageTicks.Add(40);
        frameData.StageTimerStartTicks.AddRange([0, 40]);

        var mark = ReplayShared.CreateMark(frameData);

        Record(frameData, 40);
        frameData.NewStageTicks.Add(80);
        frameData.StageTimerStartTicks.Add(80);
        // Re-entering a stage overwrites its start tick in place, so the mark must keep its own copy.
        frameData.StageTimerStartTicks[1] = 70;

        Assert.True(ReplayShared.TryRewindFrames(frameData, mark));

        Assert.Equal(60, frameData.Frames.Count);
        Assert.Equal(59, frameData.Frames[59].Origin.X);
        Assert.Equal(new[] { 40 }, frameData.NewStageTicks);
        Assert.Equal(new[] { 0, 40 }, frameData.StageTimerStartTicks);
    }

    [Fact]
    public void RewindRefusesAMarkFromAnotherLineage()
    {
        var frameData = Recording(60);
        var mark      = ReplayShared.CreateMark(frameData);

        // A timer start or idle trim cut frames off the front, so the mark's indices no longer line up.
        frameData.Lineage++;
        Record(frameData, 10);

        Assert.False(ReplayShared.CanRewind(frameData, mark));
        Assert.False(ReplayShared.TryRewindFrames(frameData, mark));
        Assert.Equal(70, frameData.Frames.Count);
    }

    [Fact]
    public void RewindRefusesAMarkPastTheRecording()
    {
        var frameData = Recording(30);
        var earlier   = ReplayShared.CreateMark(frameData);

        Record(frameData, 30);
        var later = ReplayShared.CreateMark(frameData);

        Assert.True(ReplayShared.TryRewindFrames(frameData, earlier));

        // The frames after the earlier mark were dropped, so the later mark points past the recording.
        Assert.False(ReplayShared.TryRewindFrames(frameData, later));
        Assert.Equal(30, frameData.Frames.Count);

        // An earlier mark stays usable: its frames are still a prefix of the recording.
        Record(frameData, 5);
        Assert.True(ReplayShared.TryRewindFrames(frameData, earlier));
        Assert.Equal(30, frameData.Frames.Count);
    }

    private static PlayerFrameData Recording(int frames)
    {
        var frameData = new PlayerFrameData { Name = "segmented" };
        Record(frameData, frames);

        return frameData;
    }

    private static void Record(PlayerFrameData frameData, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            frameData.Frames.Add(new ReplayFrameData { Origin = new Vector(frameData.Frames.Count, 0, 0) });
        }
    }
}
