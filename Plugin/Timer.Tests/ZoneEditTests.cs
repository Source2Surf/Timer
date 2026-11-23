using System;
using System.Collections.Generic;
using System.Linq;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared.Models.Zone;
using Xunit;

namespace Timer.Tests;

public sealed class ZoneEditTests
{
    [Theory]
    [InlineData("start", 0, EZoneType.Start, null)]
    [InlineData("END", 0, EZoneType.End, null)]
    [InlineData("stage 3", 0, EZoneType.Stage, 3)]
    [InlineData("stage", 0, EZoneType.Stage, null)]
    [InlineData("b1 end", 1, EZoneType.End, null)]
    [InlineData("b2 checkpoint 4", 2, EZoneType.Checkpoint, 4)]
    [InlineData("stoptimer", 0, EZoneType.StopTimer, null)]
    [InlineData("b1 reset", 1, EZoneType.Reset, null)]
    internal void ParsesZoneArguments(string input, int track, EZoneType type, int? number)
    {
        Assert.True(ZoneEdit.TryParse(input.Split(' '), out var t, out var z, out var n));
        Assert.Equal((track, type, number), (t, z, n));
    }

    [Theory]
    [InlineData("")]
    [InlineData("b0 start")]           // track 0 is the main track, not a bonus
    [InlineData("b99 start")]          // past MAX_TRACK
    [InlineData("b1")]                 // no type
    [InlineData("2")]                  // a number isn't a type
    [InlineData("invalid")]
    [InlineData("max")]
    [InlineData("start 2")]            // only stages and checkpoints take a number
    [InlineData("stage 1")]            // stage 1 is the start zone
    [InlineData("checkpoint 0")]
    [InlineData("checkpoint 100")]
    [InlineData("stage x")]
    public void RejectsBadArguments(string input)
        => Assert.False(ZoneEdit.TryParse(input.Length == 0 ? [] : input.Split(' '), out _, out _, out _));

    [Fact]
    public void NextNumberFollowsTheHighestOnTheTrack()
    {
        List<ZoneEntry> zones =
        [
            new (1, 0, EZoneType.Stage, 2, true),
            new (2, 0, EZoneType.Stage, 5, false),
            new (3, 1, EZoneType.Stage, 9, false), // another track
            new (4, 0, EZoneType.Checkpoint, 1, false),
        ];

        Assert.Equal(6, ZoneEdit.NextNumber(zones, 0, EZoneType.Stage));
        Assert.Equal(2, ZoneEdit.NextNumber(zones, 0, EZoneType.Checkpoint));
        Assert.Equal(2, ZoneEdit.NextNumber(zones, 2, EZoneType.Stage));      // stages start at 2
        Assert.Equal(1, ZoneEdit.NextNumber(zones, 2, EZoneType.Checkpoint));
        Assert.Equal(0, ZoneEdit.NextNumber(zones, 0, EZoneType.End));
    }

    [Theory]
    [InlineData(0, EZoneType.Start, 0, true)]
    [InlineData(0, EZoneType.Stage, 2, true)]
    [InlineData(0, EZoneType.Stage, 0, false)]
    [InlineData(0, EZoneType.End, 3, false)]
    [InlineData(-1, EZoneType.Start, 0, false)]
    [InlineData(0, EZoneType.Invalid, 0, false)]
    internal void ValidatesZones(int track, EZoneType type, int number, bool valid)
        => Assert.Equal(valid, ZoneEdit.IsValid(track, type, number));

    [Fact]
    public void ListsTracksThenStartStagesCheckpointsEnd()
    {
        List<ZoneEntry> zones =
        [
            new (10, 1, EZoneType.Start, 0, false),
            new (11, 0, EZoneType.End, 0, true),
            new (12, 0, EZoneType.Checkpoint, 1, false),
            new (13, 0, EZoneType.Stage, 3, false),
            new (14, 0, EZoneType.Stage, 2, false),
            new (15, 0, EZoneType.Start, 0, false),
            new (16, 0, EZoneType.Start, 0, true),
        ];

        zones.Sort(ZoneEdit.Compare);

        // The map's own start zone before the one added in game.
        Assert.Equal([16u, 15u, 14u, 13u, 12u, 11u, 10u], zones.Select(z => z.Id));
    }

    // As the engine turns a SOLID_OBB trigger: yaw 90 lays a box long along X along Y.
    [Fact]
    public void TurningAnticlockwiseLaysXAlongY()
    {
        var corner1 = new Vector(-100, -20, 0);
        var corner2 = new Vector(100, 20, 100);

        Assert.True(ZoneEdit.Contains(corner1, corner2, 0f, new Vector(70, 0, 50)));
        Assert.False(ZoneEdit.Contains(corner1, corner2, 0f, new Vector(0, 70, 50)));

        Assert.False(ZoneEdit.Contains(corner1, corner2, 90f, new Vector(70, 0, 50)));
        Assert.True(ZoneEdit.Contains(corner1, corner2, 90f, new Vector(0, 70, 50)));
        Assert.False(ZoneEdit.Contains(corner1, corner2, 90f, new Vector(0, 70, 150))); // above it

        var turned = ZoneEdit.Corners(corner1, corner2, 90f);
        Assert.All(turned, c => Assert.InRange(MathF.Abs(c.X), 0f, 20.001f));
        Assert.Equal([0f, 0f, 0f, 0f, 100f, 100f, 100f, 100f], turned.Select(c => c.Z));

        var point = ZoneEdit.Turn(new Vector(10, 0, 5), new Vector(), 90f);
        Assert.Equal(0f, point.X, 3);
        Assert.Equal(10f, point.Y, 3);
        Assert.Equal(5f, point.Z);
    }

    // A turn about a centre off the origin keeps the centre where it is.
    [Fact]
    public void TurnsAboutTheBoxCentre()
    {
        var corner1 = new Vector(1000, 2000, 0);
        var corner2 = new Vector(1200, 2040, 64);

        Assert.True(ZoneEdit.Contains(corner1, corner2, 45f, new Vector(1100, 2020, 32)));
        Assert.True(ZoneEdit.Contains(corner1, corner2, 90f, new Vector(1100, 2100, 32)));
        Assert.False(ZoneEdit.Contains(corner1, corner2, 90f, new Vector(1180, 2020, 32)));
    }

    [Theory]
    [InlineData(-15f, 345f)]
    [InlineData(360f, 0f)]
    [InlineData(725f, 5f)]
    [InlineData(90f, 90f)]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    public void NormalizesYaw(float yaw, float normal)
        => Assert.Equal(normal, ZoneEdit.NormalizeYaw(yaw), 3);

    [Theory]
    [InlineData("1 -2 3.5", true, 1f, -2f, 3.5f)]
    [InlineData("0 0 -16", true, 0f, 0f, -16f)]
    [InlineData("1 2", false, 0f, 0f, 0f)]
    [InlineData("1 2 3 4", false, 0f, 0f, 0f)]
    [InlineData("x 2 3", false, 0f, 0f, 0f)]
    [InlineData("NaN 0 0", false, 0f, 0f, 0f)]
    [InlineData("1e40 0 0", false, 0f, 0f, 0f)]
    public void ParsesMoves(string input, bool ok, float x, float y, float z)
    {
        Assert.Equal(ok, ZoneEdit.TryParseMove(input.Split(' '), out var delta));

        if (ok)
        {
            Assert.Equal((x, y, z), (delta.X, delta.Y, delta.Z));
        }
    }

    [Theory]
    [InlineData("15", true, 15f)]
    [InlineData("-90", true, -90f)]
    [InlineData("1,5", false, 0f)]
    [InlineData("15 30", false, 0f)]
    public void ParsesTurns(string input, bool ok, float degrees)
    {
        Assert.Equal(ok, ZoneEdit.TryParseTurn(input.Split(' '), out var turn));
        Assert.Equal(degrees, turn);
    }

    // bhoptimer's adjuster on an unturned box: the point's coordinate moves, the other point stays.
    [Fact]
    public void ResizingMovesOnlyThatPoint()
    {
        var corner1 = new Vector(0, 0, 0);
        var corner2 = new Vector(100, 40, 128);

        var (c1, c2) = ZoneEdit.Resize(corner1, corner2, 0f, 1, 0, -16)!.Value;
        Assert.Equal((-16f, 0f, 0f), (c1.X, c1.Y, c1.Z));
        Assert.Equal((100f, 40f, 128f), (c2.X, c2.Y, c2.Z));

        (c1, c2) = ZoneEdit.Resize(corner1, corner2, 0f, 2, 2, 32)!.Value;
        Assert.Equal((0f, 0f, 0f), (c1.X, c1.Y, c1.Z));
        Assert.Equal((100f, 40f, 160f), (c2.X, c2.Y, c2.Z));
    }

    // Turned, the point moves along the box's own axis and the other point's side stays where it is on the map.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    public void ResizingATurnedBoxKeepsTheOtherSide(int point, int axis)
    {
        var corner1 = new Vector(1000, 2000, 0);
        var corner2 = new Vector(1200, 2040, 64);
        const float yaw = 30f;

        var (c1, c2) = ZoneEdit.Resize(corner1, corner2, yaw, point, axis, 24)!.Value;

        var before = ZoneEdit.Corners(corner1, corner2, yaw);
        var after  = ZoneEdit.Corners(c1, c2, yaw);

        // The corners on the other point's side, before and after, by the same index: unmoved.
        var stays = point == 1 ? corner2 : corner1;
        var kept  = 0;

        for (var i = 0; i < 8; i++)
        {
            var local = ZoneEdit.Turn(before[i], (corner1 + corner2) / 2f, -yaw);

            if (MathF.Abs(local[axis] - stays[axis]) > 0.01f)
            {
                continue;
            }

            kept++;
            Assert.Equal(before[i].X, after[i].X, 2);
            Assert.Equal(before[i].Y, after[i].Y, 2);
            Assert.Equal(before[i].Z, after[i].Z, 2);
        }

        Assert.Equal(4, kept);

        // And on that axis the moved point is 24 further on: the box is as long as from it to the other point.
        var moved = (point == 1 ? corner1 : corner2)[axis] + 24;
        Assert.Equal(MathF.Abs(moved - stays[axis]), MathF.Abs(c2[axis] - c1[axis]), 2);
    }

    [Fact]
    public void ResizingRefusesABoxThinnerThanAUnit()
    {
        var corner1 = new Vector(0, 0, 0);
        var corner2 = new Vector(16, 16, 16);

        Assert.Null(ZoneEdit.Resize(corner1, corner2, 0f, 2, 0, -16));
        Assert.Null(ZoneEdit.Resize(corner1, corner2, 0f, 3, 0, 8));
        Assert.Null(ZoneEdit.Resize(corner1, corner2, 0f, 1, 3, 8));
        Assert.NotNull(ZoneEdit.Resize(corner1, corner2, 0f, 2, 0, -15));
    }

    [Theory]
    [InlineData("1 x 16", true, 1, 0, 16f)]
    [InlineData("2 Z -8", true, 2, 2, -8f)]
    [InlineData("2 y 0.5", true, 2, 1, 0.5f)]
    [InlineData("3 x 16", false, 0, 0, 0f)]
    [InlineData("1 w 16", false, 0, 0, 0f)]
    [InlineData("1 xy 16", false, 0, 0, 0f)]
    [InlineData("1 x", false, 0, 0, 0f)]
    [InlineData("1 x NaN", false, 0, 0, 0f)]
    public void ParsesResizes(string input, bool ok, int point, int axis, float by)
    {
        Assert.Equal(ok, ZoneEdit.TryParseResize(input.Split(' '), out var p, out var a, out var b));

        if (ok)
        {
            Assert.Equal((point, axis, by), (p, a, b));
        }
    }
}
