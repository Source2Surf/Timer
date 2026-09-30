using Timer.Backend.Endpoints;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class ApiRouteValidationTests
{
    [Fact]
    public void MapNameIsTrimmedAndCanonicalized()
    {
        var valid = ApiRouteValidation.TryNormalizeMapName("  SURF_Utopia  ", out var mapName, out _);

        Assert.True(valid);
        Assert.Equal("surf_utopia", mapName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("surf/test")]
    [InlineData("surf\\test")]
    [InlineData("surf\ntest")]
    [InlineData("surf_édge")]
    [InlineData("surf edge")]
    public void InvalidMapNameIsRejected(string value)
        => Assert.False(ApiRouteValidation.TryNormalizeMapName(value, out _, out _));

    [Theory]
    [InlineData(null, 5000)]
    [InlineData("1", 1)]
    [InlineData("5000", 5000)]
    public void RecordLimitAcceptsDocumentedRange(string? raw, int expected)
    {
        Assert.True(ApiRouteValidation.TryParseLimit(raw, out var limit, out _));
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("5001")]
    [InlineData("+1")]
    [InlineData("1.0")]
    public void RecordLimitRejectsValuesOutsideDocumentedRange(string raw)
        => Assert.False(ApiRouteValidation.TryParseLimit(raw, out _, out _));

    [Theory]
    [InlineData("0")]
    [InlineData("18446744073709551616")]
    [InlineData("-1")]
    [InlineData(" 1 ")]
    public void SteamIdRejectsNonCanonicalOrZeroValues(string raw)
        => Assert.False(ApiRouteValidation.TryParseSteamId(raw, out _, out _));

    [Fact]
    public void SteamIdAcceptsMaximumUnsignedValue()
    {
        Assert.True(ApiRouteValidation.TryParseSteamId("18446744073709551615", out var value, out _));
        Assert.Equal(ulong.MaxValue, value);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("15", true)]
    [InlineData("16", false)]
    [InlineData("-1", false)]
    public void StyleUsesTimerBounds(string raw, bool expected)
        => Assert.Equal(expected, ApiRouteValidation.TryParseOptionalStyle(raw, out _, out _));

    [Theory]
    [InlineData("1", true)]
    [InlineData("63", true)]
    [InlineData("0", false)]
    [InlineData("64", false)]
    public void StageExcludesZeroAndMaximum(string raw, bool expected)
        => Assert.Equal(expected, ApiRouteValidation.TryParseOptionalStage(raw, out _, out _));
}
