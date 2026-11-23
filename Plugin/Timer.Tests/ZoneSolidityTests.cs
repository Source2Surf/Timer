using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Source2Surf.Timer.Modules.Zone;
using Xunit;

namespace Timer.Tests;

public sealed class ZoneSolidityTests
{
    // An axis-aligned box as Rubikon stores a hull: outward faces, Normal·p <= Offset inside.
    private static SolidHull Box(Vector3 min, Vector3 max)
    {
        SolidPlane[] planes =
        [
            new (Vector3.UnitX, max.X), new (-Vector3.UnitX, -min.X),
            new (Vector3.UnitY, max.Y), new (-Vector3.UnitY, -min.Y),
            new (Vector3.UnitZ, max.Z), new (-Vector3.UnitZ, -min.Z),
        ];

        var corners = new List<Vector3>();

        foreach (var x in new[] { min.X, max.X })
        foreach (var y in new[] { min.Y, max.Y })
        foreach (var z in new[] { min.Z, max.Z })
        {
            corners.Add(new Vector3(x, y, z));
        }

        return new SolidHull(planes, corners.ToArray());
    }

    // The same box as a closed triangle mesh, or without its lid, or only its bottom; separate, each triangle has its own
    // corners, as a mesh may repeat them.
    private static SolidMesh BoxMesh(Vector3 min, Vector3 max, bool bottomOnly = false, bool noLid = false, bool separate = false)
    {
        Vector3[] v =
        [
            new (min.X, min.Y, min.Z), new (max.X, min.Y, min.Z), new (max.X, max.Y, min.Z), new (min.X, max.Y, min.Z),
            new (min.X, min.Y, max.Z), new (max.X, min.Y, max.Z), new (max.X, max.Y, max.Z), new (min.X, max.Y, max.Z),
        ];

        int[] bottom = [0, 2, 1, 0, 3, 2];
        int[] lid    = [4, 5, 6, 4, 6, 7];
        int[] sides  = [0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        int[] t      = bottomOnly ? bottom : noLid ? [.. bottom, .. sides] : [.. bottom, .. lid, .. sides];

        return separate ? new SolidMesh(t.Select(i => v[i]).ToArray(), Enumerable.Range(0, t.Length).ToArray()) : new SolidMesh(v, t);
    }

    // Plates 1 unit thick on every side of a box, around nothing.
    private static List<SolidHull> Shell(Vector3 lo, Vector3 hi)
        =>
        [
            Box(lo, new Vector3(hi.X, hi.Y, lo.Z + 1)),
            Box(lo, new Vector3(lo.X + 1, hi.Y, hi.Z)),
            Box(new Vector3(hi.X - 1, lo.Y, lo.Z), hi),
            Box(lo, new Vector3(hi.X, lo.Y + 1, hi.Z)),
            Box(new Vector3(lo.X, hi.Y - 1, lo.Z), hi),
            Box(new Vector3(lo.X, lo.Y, hi.Z - 1), hi),
        ];

    private static readonly Vector3 Lo = new (-200, -200, 0);
    private static readonly Vector3 Hi = new (200, 200, 128);

    [Fact]
    public void ABoxIsSolid()
    {
        var report = ZoneSolidityCheck.Measure([Box(Lo, Hi)], []);

        Assert.Equal(ZoneSolidity.Solid, report.Kind);
        Assert.False(report.CanRebuildConvex);
    }

    // surf_ravine's bonus 1 start: a floor and walls 1 unit thick around nothing.
    [Fact]
    public void PlatesAroundAnEmptyBoxAreHollowAndRebuildable()
    {
        var report = ZoneSolidityCheck.Measure(Shell(Lo, Hi), []);

        Assert.Equal(ZoneSolidity.Hollow, report.Kind);
        Assert.True(report.HollowPercent > 90);
        Assert.False(report.HoldsInside);
        Assert.True(report.CanRebuildConvex);
    }

    // bhop_lego3's zones: closed meshes, touched anywhere inside.
    [Fact]
    public void AClosedMeshHoldsItsInside()
    {
        var report = ZoneSolidityCheck.Measure([], [BoxMesh(Lo, Hi)]);

        Assert.Equal(ZoneSolidity.Mesh, report.Kind);
        Assert.True(report.HoldsInside);
        Assert.False(report.CanRebuildConvex);
    }

    [Fact]
    public void AMeshRepeatingItsCornersIsStillClosed()
        => Assert.Equal(ZoneSolidity.Mesh, ZoneSolidityCheck.Measure([], [BoxMesh(Lo, Hi, separate: true)]).Kind);

    [Fact]
    public void ABoxMeshWithoutItsLidIsOpen()
    {
        var report = ZoneSolidityCheck.Measure([], [BoxMesh(Lo, Hi, noLid: true)]);

        Assert.Equal(ZoneSolidity.MeshOpen, report.Kind);
        Assert.False(report.HoldsInside);
        Assert.False(report.CanRebuildConvex);
    }

    // A sheet or a plate gives no height to rebuild with.
    [Fact]
    public void SheetsAndPlatesStayAsTheyAre()
    {
        var sheet = ZoneSolidityCheck.Measure([], [BoxMesh(Lo, Hi, bottomOnly: true)]);
        var plate = ZoneSolidityCheck.Measure([Box(Lo, new Vector3(Hi.X, Hi.Y, Lo.Z + 8))], []);

        Assert.Equal(ZoneSolidity.MeshOpen, sheet.Kind);
        Assert.Equal(ZoneSolidity.Thin, plate.Kind);
        Assert.False(sheet.CanRebuildConvex);
        Assert.False(plate.CanRebuildConvex);
    }

    // A solid L of two boxes has no gaps; its footprint isn't convex, so one hull around it would fill the corner.
    [Fact]
    public void AnLOfSolidBoxesIsSolidWithAConcaveFootprint()
    {
        List<SolidHull> l =
        [
            Box(Lo, new Vector3(Hi.X, -100, Hi.Z)),
            Box(Lo, new Vector3(-100, Hi.Y, Hi.Z)),
        ];

        var report = ZoneSolidityCheck.Measure(l, []);

        Assert.Equal(ZoneSolidity.Solid, report.Kind);
        Assert.InRange(report.FootprintConvexPercent, 0, 80);
    }

    // The same L as shells: hollow, but not one convex hull.
    [Fact]
    public void AnLOfShellsIsNotRebuildableAsOneHull()
    {
        var report = ZoneSolidityCheck.Measure([.. Shell(Lo, new Vector3(Hi.X, -100, Hi.Z)), .. Shell(Lo, new Vector3(-100, Hi.Y, Hi.Z))], []);

        Assert.Equal(ZoneSolidity.Hollow, report.Kind);
        Assert.False(report.CanRebuildConvex);
    }
}
