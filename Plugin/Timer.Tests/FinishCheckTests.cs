using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules;
using Xunit;

namespace Timer.Tests;

// What the End zone checks before a run counts: the last stages and checkpoints, which nothing after them catches.
public sealed class FinishCheckTests
{
    [Theory]
    [InlineData(0, 0, 0, 0)]  // linear, no checkpoints
    [InlineData(4, 4, 0, 0)]  // every stage
    [InlineData(4, 0, 0, 0)]  // no stage timer running
    [InlineData(0, 0, 3, 3)]  // every checkpoint
    [InlineData(0, 0, 3, -1)] // no run timer running
    [InlineData(1, 1, 0, 0)]  // one stage is a linear track
    public void ACompleteRunFinishes(int stages, int stage, int lastCheckpoint, int checkpoint)
        => Assert.Null(TimerModule.MissedBeforeEnd(stages, stage, lastCheckpoint, checkpoint));

    [Theory]
    [InlineData(4, 3)]
    [InlineData(4, 1)]
    public void SkippingTheLastStagesStopsIt(int stages, int stage)
        => Assert.Same(ChatTexts.MissingStages, TimerModule.MissedBeforeEnd(stages, stage, 0, 0));

    [Theory]
    [InlineData(3, 2)]
    [InlineData(3, 0)]
    public void SkippingTheLastCheckpointsStopsIt(int lastCheckpoint, int checkpoint)
        => Assert.Same(ChatTexts.MissingCheckpoints, TimerModule.MissedBeforeEnd(0, 0, lastCheckpoint, checkpoint));
}
