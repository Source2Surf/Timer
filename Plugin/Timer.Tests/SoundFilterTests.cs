using Source2Surf.Timer.Modules;
using Xunit;

namespace Timer.Tests;

// Sound names as CS2's soundevents (game_sounds_footsteps, game_sounds_weapons) define them.
public sealed class SoundFilterTests
{
    [Theory]
    [InlineData("CT_Concrete.StepLeft")]
    [InlineData("T_Default.StepLeft")]
    [InlineData("Land_Concrete.StepLeft")]
    [InlineData("Heavy.Step")]
    [InlineData("Gear.JumpLand.CT")]
    [InlineData("Land_Wet.Splash")]
    [InlineData("Player.Wade")]
    public void FootstepsAreFootsteps(string sound)
        => Assert.Equal(SoundFilterModule.SoundKind.Footstep, SoundFilterModule.Classify(sound));

    [Theory]
    [InlineData("Weapon_AK47.Single")]
    [InlineData("Weapon_Knife.Deploy")]
    [InlineData("HEGrenade.Throw")]
    [InlineData("Flashbang.Bounce")]
    public void WeaponSoundsAreWeaponSounds(string sound)
        => Assert.Equal(SoundFilterModule.SoundKind.Weapon, SoundFilterModule.Classify(sound));

    [Theory]
    [InlineData("Player.DamageHelmet")]
    [InlineData("UIPanorama.round_report_round_won")]
    [InlineData("Buttons.snd9")]
    public void TheRestIsLeftAlone(string sound)
        => Assert.Equal(SoundFilterModule.SoundKind.Other, SoundFilterModule.Classify(sound));
}
