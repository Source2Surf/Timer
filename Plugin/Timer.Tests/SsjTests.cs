using Source2Surf.Timer.Modules.Hud;
using Xunit;

namespace Timer.Tests;

public sealed class SsjTrackerTests
{
    private const float AirMaxWish = 30f;
    private const float Strafe     = 250f; // a full key, in units

    private static long _accel;

    private static SsjJump? Step(SsjTracker t,
                                 bool       ground,
                                 float      vx,
                                 float      vy     = 0,
                                 float      fwd    = 0,
                                 float      side   = 0,
                                 float      z      = 0,
                                 bool       jump   = false,
                                 float      yaw    = 0,
                                 float      weight = 1,
                                 long?      accel  = null)
    {
        t.BeginStep(vx, vy, ground);

        if (jump)
        {
            t.Jumped();
        }

        return t.EndStep(weight, yaw, fwd, side, AirMaxWish, z, accel ?? ++_accel);
    }

    // Jumping from the ground: the step's inputs still count toward the jump before it.
    private static SsjJump Jump(SsjTracker t, float speed = 300, float z = 0, float side = 0, float yaw = 0)
        => Step(t, true, speed, side: side, z: z, jump: true, yaw: yaw)!;

    [Fact]
    public void PerpendicularStrafesGainFully()
    {
        // Facing +x at 300 u/s, holding A: the wish direction is +y.
        Assert.Equal((1f, true), SsjTracker.StepGain(300, 0, 0, 0, Strafe, AirMaxWish));
    }

    [Fact]
    public void SideMoveIsToTheLeft()
    {
        // Facing +y, left is -x: moving along it there's nothing to add, across it everything.
        Assert.Equal((0f, false), SsjTracker.StepGain(-300, 0, 90, 0, Strafe, AirMaxWish));

        var (gain, synced) = SsjTracker.StepGain(0, 300, 90, 0, Strafe, AirMaxWish)!.Value;
        Assert.Equal(1f, gain, 3);
        Assert.True(synced);
    }

    [Fact]
    public void PushingAlongTheVelocityGainsNothingAndAgainstItLoses()
    {
        Assert.Equal((0f, false), SsjTracker.StepGain(300, 0, 0, Strafe, 0, AirMaxWish));

        // Still synced, as in bhop-get-stats: the step accelerates, just the wrong way.
        var (gain, synced) = SsjTracker.StepGain(300, 0, 0, -Strafe, 0, AirMaxWish)!.Value;
        Assert.True(gain < 0);
        Assert.True(synced);
    }

    [Fact]
    public void NoKeysIsNoStrafe()
        => Assert.Null(SsjTracker.StepGain(300, 0, 0, 0, 0, AirMaxWish));

    [Fact]
    public void TheFirstJumpOfAChainHasOnlyItsSpeed()
    {
        var t    = new SsjTracker();
        var jump = Jump(t, 276.8f);

        Assert.Equal(1, jump.Number);
        Assert.Equal(1, jump.Chain);
        Assert.Equal(276, jump.Speed); // rounded down, from the speed the jump's step started with
        Assert.Null(jump.Stats);
    }

    [Fact]
    public void TheNextJumpMeasuresTheAirTimeBetween()
    {
        var t = new SsjTracker();
        Jump(t, 300, 64);

        // 20 ticks strafing perfectly and 20 without keys, the last being the next jump's own step.
        for (var i = 0; i < 20; i++)
        {
            Step(t, false, 310, side: Strafe);

            if (i < 19)
            {
                Step(t, false, 310);
            }
        }

        var jump = Jump(t, 350, 80);

        Assert.Equal(2, jump.Number);
        var stats = jump.Stats!.Value;
        Assert.Equal(50, stats.SpeedDiff, 3);
        Assert.Equal(16, stats.HeightDiff, 3);
        Assert.Equal(0.5f, stats.Gain, 3);
        Assert.Equal(0.5f, stats.Sync, 3);
        Assert.Equal(0, stats.Strafes); // A held throughout reverses nothing
    }

    [Fact]
    public void AStraightPathKeepsEfficiencyAtTheGain()
    {
        var t = new SsjTracker();
        Jump(t);

        for (var i = 0; i < 10; i++)
        {
            Step(t, false, 300, side: Strafe);
        }

        var stats = Jump(t, side: Strafe).Stats!.Value;
        Assert.Equal(1f, stats.Gain, 3);
        Assert.Equal(1f, stats.Efficiency, 3);
    }

    [Fact]
    public void ACurvedPathLowersEfficiencyButNotGain()
    {
        var t = new SsjTracker();
        Jump(t);

        // A quarter circle at 300 u/s, always strafing across the velocity.
        for (var i = 0; i < 32; i++)
        {
            var (sin, cos) = System.MathF.SinCos(i * (System.MathF.PI / 2 / 32));
            Step(t, false, 300 * cos, 300 * sin, side: Strafe, yaw: i * (90f / 32));
        }

        // Taking off along the last direction, still strafing across it.
        var stats = Step(t, true, 0, 300, side: Strafe, jump: true, yaw: 90)!.Stats!.Value;
        Assert.Equal(1f, stats.Gain, 3);
        Assert.InRange(stats.Efficiency, 0.85f, 0.95f); // chord / arc of a quarter circle is 0.90
    }

    [Fact]
    public void EachReversedKeyIsAStrafe()
    {
        var t = new SsjTracker();
        Jump(t, side: -Strafe); // D held into the jump

        // A (reverses D), release, A again (no), D, D, A: three.
        float[] sides = [Strafe, 0, Strafe, -Strafe, -Strafe, Strafe];

        foreach (var side in sides)
        {
            Step(t, false, 300, side: side);
        }

        Assert.Equal(3, Jump(t, side: Strafe).Stats!.Value.Strafes);
    }

    [Fact]
    public void ReversalsCarryOverFromTheJumpBefore()
    {
        var t = new SsjTracker();
        Jump(t);
        Step(t, false, 300, side: Strafe);
        Jump(t, side: Strafe);

        // D right after the second jump reverses the A held through it.
        Step(t, false, 300, side: -Strafe);

        Assert.Equal(1, Jump(t, side: -Strafe).Stats!.Value.Strafes);
    }

    [Fact]
    public void SidewaysStrafesCountForwardAndBack()
    {
        var t = new SsjTracker();
        Jump(t);

        // W (nothing to reverse yet), S, W.
        foreach (var fwd in new[] { Strafe, -Strafe, Strafe })
        {
            Step(t, false, 0, 300, fwd: fwd);
        }

        Assert.Equal(2, Jump(t).Stats!.Value.Strafes);
    }

    [Fact]
    public void SubTickStepsCountByTheirShareOfTheTick()
    {
        var t = new SsjTracker();
        Jump(t);

        // Half a tick without keys between two whole ticks strafing perfectly (the second is the jump's).
        Step(t, false, 300, weight: 0.5f);
        Step(t, false, 300, side: Strafe);

        var stats = Jump(t, side: Strafe).Stats!.Value;
        Assert.Equal(0.8f, stats.Gain, 3);
        Assert.Equal(0.8f, stats.Sync, 3);
    }

    [Fact]
    public void StepsSplitFromOneAccelerationScoreOnce()
    {
        var t = new SsjTracker();
        Jump(t);

        // The first half reaches wishspd along the wish direction, so the second half alone would score nothing.
        Step(t, false, 300, side: Strafe, weight: 0.5f, accel: -1);
        Step(t, false, 300, 30, side: Strafe, weight: 0.5f, accel: -1);

        var stats = Jump(t, side: Strafe).Stats!.Value;
        Assert.Equal(1f, stats.Gain, 3);
        Assert.Equal(1f, stats.Sync, 3);
    }

    [Fact]
    public void LandingBrieflyKeepsTheChain()
    {
        var t = new SsjTracker();
        Jump(t);
        Step(t, false, 300);

        for (var i = 0; i < 5; i++)
        {
            Step(t, true, 300);
        }

        Assert.Equal(2, Jump(t).Number);
    }

    [Fact]
    public void TenTicksOnTheGroundStartANewChain()
    {
        var t = new SsjTracker();
        Jump(t);
        Step(t, false, 300);

        for (var i = 0; i < (int) SsjTracker.ChainGroundTicks; i++)
        {
            Step(t, true, 0);
        }

        Assert.Equal(0, t.Jump);

        var jump = Jump(t, 0);
        Assert.Equal(1, jump.Number);
        Assert.Equal(2, jump.Chain);
    }

    [Fact]
    public void NoclipOrALadderEndsTheChain()
    {
        var t = new SsjTracker();
        Jump(t);
        t.Break();

        Assert.Equal(0, t.Jump);
        Assert.False(t.InStep);
        Assert.Equal(1, Jump(t, 0).Number);
    }

    [Fact]
    public void ViewersCatchUpOnTheJumpsStillKept()
    {
        var t = new SsjTracker();
        Jump(t);

        for (var i = 0; i < 5; i++)
        {
            Step(t, false, 300);
            Jump(t);
        }

        Assert.Equal(6, t.Serial);
        Assert.Equal(3, t.After(0)!.Serial); // the oldest still kept
        Assert.Equal(5, t.After(4)!.Serial);
        Assert.Null(t.After(6));
    }

    [Theory]
    [InlineData(6, 6, false, false, true)]
    [InlineData(12, 6, false, false, false)]
    [InlineData(12, 6, true, false, true)]
    [InlineData(13, 6, true, false, false)]
    [InlineData(1, 6, false, true, true)]
    [InlineData(1, 6, false, false, false)]
    [InlineData(1, 1, false, false, true)]  // jump 1 alone: the takeoff
    [InlineData(2, 1, false, false, false)]
    [InlineData(7, 1, true, false, true)]   // jump 1 with repeat: every jump
    public void ShowsThePickedJumps(int number, int every, bool repeat, bool first, bool shown)
        => Assert.Equal(shown, SsjTracker.Shows(number, every, repeat, first));

    // bhop-get-stats' percentage tiers: under 60 / 70 / 80 / 90, and 90 up.
    [Theory]
    [InlineData(0.40f, (int) SsjTier.ReallyBad)]
    [InlineData(0.60f, (int) SsjTier.Bad)]
    [InlineData(0.75f, (int) SsjTier.Meh)]
    [InlineData(0.80f, (int) SsjTier.Good)]
    [InlineData(0.90f, (int) SsjTier.ReallyGood)]
    [InlineData(1.00f, (int) SsjTier.ReallyGood)]
    [InlineData(float.NaN, (int) SsjTier.ReallyBad)]
    public void TiersFollowTheBarsReached(float value, int expected)
        => Assert.Equal(expected, (int) HudFormat.Tier(value, [0.60f, 0.70f, 0.80f, 0.90f]));

    [Fact]
    public void ShowsAtOnceAndOnlyFadesGoing()
    {
        (bool Gone, bool Snap) last = (false, false);

        (bool Gone, bool Snap) At(float shownFor, bool menu = false)
            => last = Source2Surf.Timer.Modules.HudModule.SsjFade(shownFor, menu, last.Snap, last.Gone);

        Assert.Equal((false, false), At(1f));   // up
        Assert.Equal((true, false), At(3.1f));  // fading out
        Assert.Equal((true, true), At(3.6f));   // faded: transitions off
        Assert.Equal((false, true), At(0f));    // the next jump: shows with transitions still off
        Assert.Equal((false, false), At(0.1f)); // shown: back on for the next fade
        Assert.Equal((false, false), At(9f, menu: true));
        Assert.Equal((true, true), At(-5f));    // a time from before a map change
    }

    [Theory]
    [InlineData(0.8124f, "81.2%")]
    [InlineData(1f, "100.0%")]
    [InlineData(-0.25f, "-25.0%")]
    [InlineData(float.NaN, "0.0%")]
    public void FormatsPercentages(float fraction, string expected)
        => Assert.Equal(expected, HudFormat.Percent(fraction));

    [Theory]
    [InlineData(12.4f, "+12")]
    [InlineData(-3.6f, "-4")]
    [InlineData(0f, "+0")]
    public void FormatsSignedChanges(float value, string expected)
        => Assert.Equal(expected, HudFormat.Signed(value));
}
