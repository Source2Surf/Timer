using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

// The start zone's jump limit: the map's max_jumps, then the style's prejumps, then the game mode's; -1 is no limit.
public sealed class PrejumpLimitTests
{
    [Theory]
    [InlineData(null, false, 5, 1)]  // the game mode's
    [InlineData(null, true, 5, 5)]   // the style's
    [InlineData(null, true, -1, -1)] // a style without a limit
    [InlineData(2, true, -1, 2)]     // the map's wins
    [InlineData(-1, false, 5, -1)]   // a map without a limit
    public void TheMapThenTheStyleThenTheGameModeDecide(int? map, bool custom, int style, int expected)
        => Assert.Equal(expected, TimerModule.ResolveMaxPrejumps(map, new StyleSetting { CustomPrejumps = custom, Prejumps = style }, 1));
}
