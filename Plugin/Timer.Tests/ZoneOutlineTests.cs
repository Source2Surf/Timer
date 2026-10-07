using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared.Models.Zone;
using Source2Surf.Timer.Types;
using Xunit;

namespace Timer.Tests;

// timer-zones.jsonc: how each zone type is outlined; a type without a colour isn't drawn.
public sealed class ZoneOutlineTests
{
    [Fact]
    public void StartAndEndAreDrawnByDefault()
    {
        var outlines = new ZoneOutlines();

        Assert.Equal((0f, 255f, 0f), Rgb(outlines, EZoneType.Start));
        Assert.Equal((255f, 0f, 0f), Rgb(outlines, EZoneType.End));
        Assert.Null(outlines.ColorOf(EZoneType.Stage));
        Assert.Equal(new ZoneOutline(false, new Vector(0, 255, 0), 2f), outlines.For(EZoneType.Start));
    }

    [Fact]
    public void TypesAreMatchedInAnyCaseAndColoursClamped()
    {
        var outlines = ZoneOutlines.Load(Write("""{ "colors": { "RESET": [300, -5, 120], "stage": [1, 2] } }"""), NullLogger.Instance);

        Assert.Equal((255f, 0f, 120f), Rgb(outlines, EZoneType.Reset));
        Assert.Null(outlines.ColorOf(EZoneType.Stage)); // not three numbers
        Assert.Null(outlines.ColorOf(EZoneType.Start)); // the file's colours replace the defaults
    }

    [Fact]
    public void TheServerSaysWhichTypesAreFlatAndHowWide()
    {
        var outlines = ZoneOutlines.Load(Write("""{ "colors": { "stage": [1, 2, 3] }, "flat": ["STAGE"], "width": 4 }"""), NullLogger.Instance);

        Assert.Equal(new ZoneOutline(true, new Vector(1, 2, 3), 4f), outlines.For(EZoneType.Stage));
    }

    [Fact]
    public void ABoxsBottomIsItsFourLowEdges()
    {
        var p1 = new Vector(0, 0, 0);
        var p2 = new Vector(100, 50, 80);

        List<Edge> box =
        [
            new (p1, new (100, 0, 0)), new (new (100, 0, 0), new (100, 50, 0)), new (new (100, 50, 0), new (0, 50, 0)), new (new (0, 50, 0), p1),
            new (new (0, 0, 80), new (100, 0, 80)), new (new (100, 0, 80), p2), new (p2, new (0, 50, 80)), new (new (0, 50, 80), new (0, 0, 80)),
            new (p1, new (0, 0, 80)), new (new (100, 0, 0), new (100, 0, 80)), new (new (100, 50, 0), p2), new (new (0, 50, 0), new (0, 50, 80)),
        ];

        Assert.Equal(box.Take(4), ZoneOutlines.Bottom(box));
    }

    [Fact]
    public void ASlopedBottomIsKeptAndAFlatZoneIsAllBottom()
    {
        // A wedge: its bottom runs up 20 over 100, its top is at 200.
        List<Edge> wedge =
        [
            new (new (0, 0, 0), new (100, 0, 20)), new (new (100, 0, 20), new (100, 50, 20)),
            new (new (0, 0, 200), new (100, 0, 200)), new (new (0, 0, 0), new (0, 0, 200)),
        ];

        Assert.Equal(wedge.Take(2), ZoneOutlines.Bottom(wedge));

        List<Edge> flat = [new (new (0, 0, 0), new (10, 0, 0)), new (new (10, 0, 0), new (10, 10, 0.5f))];
        Assert.Equal(flat, ZoneOutlines.Bottom(flat));
    }

    [Fact]
    public void TheShippedFileMatchesTheDefaults()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (!File.Exists(Path.Combine(root.FullName, "sharp", "configs", "timer-zones.jsonc")))
        {
            root = root.Parent!;
        }

        var shipped  = ZoneOutlines.Load(Path.Combine(root.FullName, "sharp", "configs", "timer-zones.jsonc"), NullLogger.Instance);
        var defaults = new ZoneOutlines();

        foreach (var type in Enum.GetValues<EZoneType>())
        {
            Assert.Equal(defaults.For(type), shipped.For(type));
        }
    }

    private static (float, float, float)? Rgb(ZoneOutlines outlines, EZoneType type)
        => outlines.ColorOf(type) is { } v ? (v.X, v.Y, v.Z) : null;

    private static string Write(string json)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, json);

        return path;
    }
}
