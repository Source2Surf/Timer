using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Xunit;

namespace Timer.Tests;

// !country: the opt-out is a saved player setting, on (shown) by default.
public sealed class CountryTests
{
    [Fact]
    public void TheCountryShowsByDefaultAndItsOptOutIsSaved()
    {
        var p = new HudPlayer((PlayerSlot) 0, 0f);

        Assert.True(p.IsOn(HudOptions.Country));

        p.Settings[HudOptions.Country.Index] = 1;
        var loaded = new HudPlayer((PlayerSlot) 1, 0f);
        PlayerSettingsCodec.Decode(PlayerSettingsCodec.Encode(p), loaded);

        Assert.False(loaded.IsOn(HudOptions.Country));
    }

    [Fact]
    public void OptionKeysStayUnique()
        => Assert.Equal(HudOptions.All.Length, HudOptions.All.Select(o => o.Key).Distinct().Count());
}
