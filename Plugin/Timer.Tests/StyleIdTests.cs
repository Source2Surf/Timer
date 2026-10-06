using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

// Runs and replays are stored by style id, so a style's id must not depend on where it sits in the config.
public sealed class StyleIdTests
{
    [Fact]
    public void WithoutIdsAStylesIdIsItsPlace()
    {
        var (byId, enabled) = Resolve(new () { Name = "A" }, new () { Name = "B" });

        Assert.Equal("A", byId[0]!.Name);
        Assert.Equal(1, byId[1]!.Id);
        Assert.Equal([0, 1], enabled);
    }

    [Fact]
    public void AnIdStaysWithItsStyleWhateverTheOrder()
    {
        var (byId, enabled) = Resolve(new () { Name = "B", Id = 5 }, new () { Name = "A", Id = 0 });

        Assert.Equal("A", byId[0]!.Name);
        Assert.Equal("B", byId[5]!.Name);
        Assert.Null(byId[1]);
        Assert.Equal([5, 0], enabled);
    }

    [Fact]
    public void ATakenOrOutOfRangeIdLeavesTheStyleOut()
    {
        var (byId, enabled) = Resolve(new () { Name = "A", Id = 2 }, new () { Name = "B", Id = 2 }, new () { Name = "C", Id = TimerConstants.MAX_STYLE });

        Assert.Equal("A", byId[2]!.Name);
        Assert.Single(byId.OfType<StyleSetting>());
        Assert.Equal([2], enabled);
    }

    [Fact]
    public void ADisabledStyleKeepsItsIdButCantBePicked()
    {
        var (byId, enabled) = Resolve(new () { Name = "A", Id = 0, Enabled = false }, new () { Name = "B", Id = 1 });

        Assert.Equal("A", byId[0]!.Name);
        Assert.Equal([1], enabled);
    }

    [Fact]
    public void WithNoneEnabledTheFirstIsEnabledAnyway()
    {
        var (_, enabled) = Resolve(new () { Id = 3, Enabled = false }, new () { Id = 7, Enabled = false });

        Assert.Equal([3], enabled);
    }

    [Theory]
    [InlineData(5, 1, 0)]  // next in the config's order, past the gap
    [InlineData(0, 1, 2)]
    [InlineData(2, 1, 2)]  // stops at the end
    [InlineData(5, -1, 5)] // and at the start
    [InlineData(1, 1, 5)]  // a disabled or unknown style starts from the default
    [InlineData(9, -1, 5)]
    public void SteppingGoesThroughThePickableStyles(int style, int step, int expected)
    {
        var module = Module(new () { Id = 5 }, new () { Id = 1, Enabled = false }, new () { Id = 0 }, new () { Id = 2 });

        Assert.Equal(expected, module.StepStyle(style, step));
    }

    [Theory]
    [InlineData("Sideways", 4)]
    [InlineData("SW", 4)]
    [InlineData("!n", 0)]
    [InlineData("hsw", null)] // disabled
    [InlineData("nope", null)]
    public void StylesAreFoundByNameOrCommand(string text, int? expected)
    {
        var module = Module(new () { Name = "Normal", Command = "normal;n", Id = 0 },
                            new () { Name = "Sideways", Command = "sideways;sw", Id = 4 },
                            new () { Name = "Half-sideways", Command = "hsw", Id = 5, Enabled = false });

        Assert.Equal(expected, module.FindStyle(text));
    }

    [Fact]
    public void IdAndEnabledAreReadFromTheConfig()
    {
        const string json = """[ { "name": "Sideways", "id": 4, "enabled": false } ]""";

        var style = Assert.Single(JsonSerializer.Deserialize<List<StyleSetting>>(json, Utils.DeserializerOptions)!);

        Assert.Equal(4, style.Id);
        Assert.False(style.Enabled);
        Assert.Null(style.ExtensionData);
    }

    private static (StyleSetting?[] ById, int[] Enabled) Resolve(params StyleSetting[] styles)
        => StyleModule.ResolveStyles(styles, NullLogger.Instance);

    private static StyleModule Module(params StyleSetting[] styles)
    {
        var module = (StyleModule) RuntimeHelpers.GetUninitializedObject(typeof(StyleModule));
        var (byId, enabled) = Resolve(styles);
        typeof(StyleModule).GetField("_byId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, byId);
        typeof(StyleModule).GetField("_ids", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(module, enabled);

        return module;
    }
}
