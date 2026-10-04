using System.Collections.Generic;
using System.Linq;
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
}
