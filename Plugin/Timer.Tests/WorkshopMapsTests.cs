using Source2Surf.Timer.Modules.MapInfo;
using Xunit;

namespace Timer.Tests;

public sealed class WorkshopMapsTests
{
    [Fact]
    public void FindsTheItemListedUnderTheMapName()
    {
        (ulong, string)[] maps = [(111, "surf_a"), (222, "SURF_B")];

        Assert.Equal(222ul, WorkshopMaps.FindItemId("surf_b", maps, "333,222"));
    }

    [Fact]
    public void MountedAddonsAloneAreNotTheMap()
    {
        // A local map with the HUD addon mounted: the addon isn't a map.
        (ulong, string)[] maps = [(111, "surf_a")];

        Assert.Equal(0ul, WorkshopMaps.FindItemId("surf_local", maps, "999"));
        Assert.Equal(0ul, WorkshopMaps.FindItemId("surf_local", [], "999"));
    }

    [Fact]
    public void MountedAddonPicksBetweenItemsSharingTheName()
    {
        (ulong, string)[] maps = [(111, "surf_x"), (222, "surf_x")];

        Assert.Equal(222ul, WorkshopMaps.FindItemId("surf_x", maps, "999, 222"));
        Assert.Equal(0ul, WorkshopMaps.FindItemId("surf_x", maps, "999"));
        Assert.Equal(0ul, WorkshopMaps.FindItemId("surf_x", maps, null));
    }

    [Fact]
    public void OneItemWithSeveralEntriesIsNotAmbiguous()
    {
        (ulong, string)[] maps = [(111, "surf_x"), (111, "surf_x")];

        Assert.Equal(111ul, WorkshopMaps.FindItemId("surf_x", maps, null));
    }
}
