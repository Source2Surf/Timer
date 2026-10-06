using System.Text.Json;
using Sharp.Shared.Types;
using Source2Surf.Timer;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Timer;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

// Style mechanics as bhoptimer has them: force_hsw, a_or_d_only, the per-jump velocity settings, timescale.
public sealed class StyleMechanicsTests
{
    [Theory]
    //          forward back   left   right  dropForward dropSide
    [InlineData(true, false, true, false, false, false)]  // W+A
    [InlineData(true, false, false, true, false, false)]  // W+D
    [InlineData(true, false, false, false, true, false)]  // W alone
    [InlineData(false, false, true, false, false, true)]  // A alone
    [InlineData(false, true, true, false, true, true)]    // S+A
    public void HalfSidewaysKeepsOnlyWWithAOrD(bool forward, bool back, bool left, bool right, bool dropForward, bool dropSide)
    {
        var verdict = StyleModule.Hsw(1, -1, false, forward, back, left, right);

        Assert.Equal((dropForward, dropSide), (verdict.DropForward, verdict.DropSide));
    }

    [Fact]
    public void SurfHalfSidewaysKeepsTheFirstPairUsed()
    {
        var first = StyleModule.Hsw(2, -1, false, forward: true, back: false, left: true, right: false); // W+A

        Assert.Equal(0, first.Combo);
        Assert.False(first.DropForward);

        var sameSet = StyleModule.Hsw(2, first.Combo, false, forward: false, back: true, left: false, right: true); // S+D
        Assert.False(sameSet.DropSide);

        var otherSet = StyleModule.Hsw(2, first.Combo, false, forward: true, back: false, left: false, right: true); // W+D
        Assert.True(otherSet.DropForward && otherSet.DropSide);
    }

    [Fact]
    public void SurfHalfSidewaysPlaysLikeHalfSidewaysInTheStartZone()
    {
        var verdict = StyleModule.Hsw(2, -1, true, forward: false, back: true, left: false, right: true); // S+D

        Assert.Equal(-1, verdict.Combo);
        Assert.True(verdict.DropForward);
    }

    [Fact]
    public void AOrDOnlyKeepsTheFirstSide()
    {
        var first = StyleModule.AOrD(-1, left: false, right: true);

        Assert.Equal(1, first.Combo);
        Assert.True(StyleModule.AOrD(first.Combo, left: true, right: false).DropSide);
        Assert.False(StyleModule.AOrD(first.Combo, left: false, right: true).DropSide);
    }

    [Theory]
    [InlineData(0f, 300f, 0f, false)]   // looking where they move
    [InlineData(180f, 300f, 0f, true)]  // looking back
    [InlineData(120f, 300f, 0f, false)] // sideways-ish isn't backwards
    [InlineData(150f, 300f, 0f, true)]  // past ~143°
    [InlineData(0f, 0f, 0f, true)]      // standing still
    public void BackwardsNeedsTheViewBackAlongTheVelocity(float yaw, float vx, float vy, bool counts)
        => Assert.Equal(counts, StyleModule.Backwards(yaw, new Vector(vx, vy, 0)));

    [Fact]
    public void AJumpAppliesBhoptimersVelocitySettings()
    {
        var velocity = new Vector(300, 400, 290); // 500 u/s horizontally

        Assert.Equal(400f, Horizontal(StyleModule.JumpVelocity(velocity, new StyleSetting { VelocityLimit = 400 })), 3);
        Assert.Equal(450f, Horizontal(StyleModule.JumpVelocity(velocity, new StyleSetting { VelocityMultiplier = 0.9f })), 3);
        Assert.Equal(600f, Horizontal(StyleModule.JumpVelocity(velocity, new StyleSetting { BonusVelocity = 100 })), 3);
        Assert.Equal(700f, Horizontal(StyleModule.JumpVelocity(velocity, new StyleSetting { MinVelocity = 700 })), 3);

        var jump = StyleModule.JumpVelocity(velocity, new StyleSetting { JumpMultiplier = 2, JumpBonus = 10 });
        Assert.Equal(590f, jump.Z, 3);
        Assert.Equal(500f, Horizontal(jump), 3);
    }

    [Fact]
    public void ATimescaleCountsPartOfEachTick()
    {
        var timer = new TimerInfo();

        for (var i = 0; i < 10; i++)
        {
            timer.Advance(0.5f);
        }

        Assert.Equal(5u, timer.TimerTick);

        timer.Advance(1f);
        Assert.Equal(6u, timer.TimerTick);
    }

    [Fact]
    public void TheSettingsAreReadFromTheStyleConfig()
    {
        const string json = """
                            { "force_hsw": 2, "a_or_d_only": true, "force_backwards": true,
                              "force_groundkeys": true, "gravity": 0.6, "speed": 1.5, "timescale": 0.5,
                              "velocity_limit": 400, "velocity": 0.9, "bonus_velocity": 5, "min_velocity": 600,
                              "jump_multiplier": 1.2, "jump_bonus": 30 }
                            """;

        var style = JsonSerializer.Deserialize<StyleSetting>(json, Utils.DeserializerOptions)!;

        Assert.Equal((2, true, true, true), (style.ForceHsw, style.AOrDOnly, style.ForceBackwards, style.ForceGroundKeys));
        Assert.Equal((0.6f, 1.5f, 0.5f), (style.Gravity, style.Speed, style.Timescale));
        Assert.Equal((400f, 0.9f, 5f, 600f, 1.2f, 30f),
                     (style.VelocityLimit, style.VelocityMultiplier, style.BonusVelocity, style.MinVelocity, style.JumpMultiplier, style.JumpBonus));
        Assert.True(style.ChangesJumps);
        Assert.Null(style.ExtensionData);
        Assert.False(new StyleSetting().ChangesJumps);
        Assert.Equal(1f, new StyleSetting { Timescale = 0 }.TimerScale);
    }

    private static float Horizontal(Vector v)
        => MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));
}
