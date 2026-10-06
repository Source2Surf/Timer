using System.Text.Json;
using Source2Surf.Timer;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

// timer-gamemodes.jsonc: which mode a map is in, and the defaults the hard-coded ones were.
public sealed class GameModeConfigTests
{
    [Theory]
    [InlineData("surf_beginner", "Surf", 375f)]
    [InlineData("BHOP_badges", "Bhop", 290f)]
    [InlineData("kz_map", "None", 375f)]
    public void AMapIsInTheFirstModeItsNameStartsWith(string map, string mode, float exitSpeed)
    {
        var config = new GameModesConfig().For(map);

        Assert.Equal(mode, config.GameMode.ToString());
        Assert.Equal(exitSpeed, config.ExitSpeedLimit);
    }

    [Fact]
    public void TheConfigFileIsRead()
    {
        const string json = """
                            {
                                // a comment
                                "modes": [ { "prefix": "kz", "mode": "Bhop", "wishspeed": 45, "max_prejumps": -1 } ],
                                "default": { "airaccelerate": 100 }
                            }
                            """;

        var config = JsonSerializer.Deserialize<GameModesConfig>(json, Utils.DeserializerOptions)!;

        Assert.Equal(EGameMode.Bhop, config.For("kz_x").GameMode);
        Assert.Equal(45f, config.For("kz_x").WishSpeed);
        Assert.Equal(-1, config.For("kz_x").MaxPrejumps);
        Assert.Equal(100f, config.For("surf_x").AirAccelerate);
    }

    [Fact]
    public void TheShippedFileMatchesTheDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (!File.Exists(Path.Combine(root.FullName, "sharp", "configs", "timer-gamemodes.jsonc")))
        {
            root = root.Parent!;
        }

        var shipped  = JsonSerializer.Deserialize<GameModesConfig>(File.ReadAllText(Path.Combine(root.FullName, "sharp", "configs", "timer-gamemodes.jsonc")),
                                                                   Utils.DeserializerOptions)!;
        var defaults = new GameModesConfig();

        Assert.Equal(defaults.Default with { Cvars = [] }, shipped.Default with { Cvars = [] });
        Assert.Equal(defaults.Modes.Length, shipped.Modes.Length);

        for (var i = 0; i < defaults.Modes.Length; i++)
        {
            Assert.Equal(defaults.Modes[i] with { Cvars = [] }, shipped.Modes[i] with { Cvars = [] });
            Assert.Equal(defaults.Modes[i].Cvars, shipped.Modes[i].Cvars);
        }
    }

    [Fact]
    public void AStyleWithoutWishspeedLeavesItToTheMode()
        => Assert.Null(JsonSerializer.Deserialize<StyleSetting>("""{ "name": "A" }""", Utils.DeserializerOptions)!.WishSpeed);
}
