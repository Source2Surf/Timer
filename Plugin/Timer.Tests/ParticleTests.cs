using Source2Surf.Timer.Utilities;
using Xunit;

namespace Timer.Tests;

// Resource IDs as the game's resource system makes them; the first one is in the compiled crouch_indicator.vpcf_c.
public sealed class ParticleTests
{
    [Theory]
    [InlineData("materials/particle/base_rope.vtex", 0xF67AE99F37DF16FCUL)]
    [InlineData("particles/crouch_indicator.vpcf", 0x29D902B6284599FFUL)]
    [InlineData("Particles\\Crouch_Indicator.vpcf", 0x29D902B6284599FFUL)]
    public void ResourceIdsMatchTheGame(string name, ulong id)
        => Assert.Equal(id, Particles.ResourceId(name));
}
