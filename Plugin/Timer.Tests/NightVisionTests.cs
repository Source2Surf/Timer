using Source2Surf.Timer.Modules;
using Xunit;

namespace Timer.Tests;

// !nv <n>: a brightness from 1 to 10, kept in tenths, or 0 to turn night vision off; anything else gets the usage.
public sealed class NightVisionTests
{
    [Theory]
    [InlineData("3", 30)]
    [InlineData("1.5", 15)]
    [InlineData("1", 10)]
    [InlineData("10", 100)]
    [InlineData("0", 0)]
    [InlineData("0.5", null)]
    [InlineData("10.5", null)]
    [InlineData("1,5", null)]
    [InlineData("NaN", null)]
    [InlineData("1e30", null)]
    [InlineData("on", null)]
    public void TheBrightnessIsReadInTenths(string arg, int? tenths)
        => Assert.Equal(tenths, NightVisionModule.ReadBrightness(arg));
}
