using Sharp.Shared.Units;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

// A finish's frames are only skipped when its result already says the replay can't be kept.
public sealed class ReplaySkipTests
{
    private static PendingRecordResult Result(EAttemptResult type, int attemptId)
        => new ()
        {
            RunId       = 1,
            RecordEvent = new PlayerRecordSavedEvent(new SteamID(76561198000000001), "runner", type, new RunRecord(), null, null, attemptId),
        };

    private static ReplayContent Playing(float time)
        => new () { Header = new ReplayFileHeader { Time = time }, Frames = [] };

    [Fact]
    public void ANoRecordFinishBehindAFasterReplayIsSkipped()
        => Assert.True(ReplayRecorderModule.NeedsNoReplay(Result(EAttemptResult.NoNewRecord, 3), 3, false, Playing(20), 25));

    [Fact]
    public void WithoutItsResultYetItIsCaptured() // a restart right after the finish, or a last stage with the run
        => Assert.False(ReplayRecorderModule.NeedsNoReplay(null, 3, false, Playing(20), 25));

    [Fact]
    public void AResultFromAnotherAttemptDoesntCount()
        => Assert.False(ReplayRecorderModule.NeedsNoReplay(Result(EAttemptResult.NoNewRecord, 2), 3, false, Playing(20), 25));

    [Theory]
    [InlineData(EAttemptResult.NewPersonalRecord)]
    [InlineData(EAttemptResult.NewServerRecord)]
    public void ABestIsAlwaysCaptured(EAttemptResult type)
        => Assert.False(ReplayRecorderModule.NeedsNoReplay(Result(type, 3), 3, false, Playing(20), 25));

    [Fact]
    public void KeptSlowerRunsAreCaptured()
        => Assert.False(ReplayRecorderModule.NeedsNoReplay(Result(EAttemptResult.NoNewRecord, 3), 3, true, Playing(20), 25));

    [Fact]
    public void ABoardWithoutAReplayOrAFasterOneTakesIt()
    {
        Assert.False(ReplayRecorderModule.NeedsNoReplay(Result(EAttemptResult.NoNewRecord, 3), 3, false, null, 25));
        Assert.False(ReplayRecorderModule.NeedsNoReplay(Result(EAttemptResult.NoNewRecord, 3), 3, false, Playing(30), 25));
    }
}
