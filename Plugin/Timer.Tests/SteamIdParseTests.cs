using Source2Surf.Timer.Utilities;
using Xunit;

namespace Timer.Tests;

// !profile and !wipeplayer take a SteamID in any of the usual forms.
public sealed class SteamIdParseTests
{
    [Theory]
    [InlineData("76561197960572620")]
    [InlineData("STEAM_1:0:153446")]
    [InlineData("STEAM_0:0:153446")]
    [InlineData("[U:1:306892]")]
    [InlineData("U:1:306892")]
    [InlineData("  76561197960572620  ")]
    public void EveryFormGivesTheSameSteamId64(string text)
    {
        Assert.True(SteamIds.TryParse(text, out var steamId));
        Assert.Equal(76561197960572620UL, steamId);
    }

    [Theory]
    [InlineData("Nuko")]
    [InlineData("12345")]               // too small to be a SteamID64
    [InlineData("76561197960265728")]   // account 0
    [InlineData("STEAM_1:2:5")]         // Y is 0 or 1
    [InlineData("STEAM_1:0")]
    [InlineData("[U:1:]")]
    [InlineData("")]
    public void AnythingElseIsntOne(string text)
        => Assert.False(SteamIds.TryParse(text, out _));
}
