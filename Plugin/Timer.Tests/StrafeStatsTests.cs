using Sharp.Shared.Types;
using Source2Surf.Timer.Modules;
using Xunit;

namespace Timer.Tests;

// Strafes and sync count for every style's keys, not just A/D.
public sealed class StrafeStatsTests
{
    [Theory]
    //          fwd side lastFwd lastSide strafe
    [InlineData(0f, 1f, 0f, -1f, true)]   // D → A
    [InlineData(0f, 1f, 0f, 1f, false)]   // A held
    [InlineData(1f, 0f, -1f, 0f, true)]   // sideways S → W
    [InlineData(1f, -1f, 1f, 1f, true)]   // HSW W+A → W+D
    [InlineData(-1f, -1f, 1f, 1f, true)]  // surf HSW W+A → S+D, once
    [InlineData(0f, 0f, 0f, 1f, false)]   // let go
    public void AStrafeIsAKeyReversed(float fwd, float side, float lastFwd, float lastSide, bool strafe)
        => Assert.Equal(strafe, TimerModule.IsStrafe(fwd, side, lastFwd, lastSide));

    private static readonly Vector East = new (300, 0, 0); // yaw 0

    [Theory]
    //          yaw    fwd side  pushes
    [InlineData(0f, 0f, 1f, 1f)]       // normal, A: left
    [InlineData(0f, 0f, -1f, -1f)]     // normal, D: right
    [InlineData(45f, 1f, 1f, 1f)]      // HSW W+A, looking 45° left of the velocity: left
    [InlineData(-55f, 1f, 1f, 1f)]     // still W+A after swinging past 45° right: A, so a right turn is bad
    [InlineData(50f, 1f, -1f, -1f)]    // W+D before the swing: D, so a right turn is good
    [InlineData(45f, -1f, -1f, -1f)]   // surf HSW S+D the same way: right
    [InlineData(-90f, 1f, 0f, -1f)]    // sideways W, looking right of the velocity: right
    [InlineData(180f, 0f, 1f, -1f)]    // backwards A: right
    [InlineData(0f, 1f, 0f, 0f)]       // W along the velocity isn't a strafe
    [InlineData(0f, 0f, 0f, 0f)]       // no keys
    public void SyncTurnsToTheSideTheKeysPush(float yaw, float fwd, float side, int pushes)
        => Assert.Equal(pushes, TimerModule.PushSide(yaw, East, fwd, side));

    [Fact]
    public void StandingStillPushesNowhere()
        => Assert.Equal(0, TimerModule.PushSide(0f, new Vector(0, 0, 0), 0f, 1f));

    [Theory]
    [InlineData(10f, 0f, 10f)]
    [InlineData(-179f, 179f, 2f)]   // turning left through 180°
    [InlineData(179f, -179f, -2f)]  // and right
    public void YawDeltaKeepsItsSignThroughTheWrap(float a, float b, float delta)
        => Assert.Equal(delta, TimerModule.YawDelta(a, b), 3);
}
