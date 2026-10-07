using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Modules;
using Xunit;

namespace Timer.Tests;

// timer-sounds.jsonc: which sound a new best plays, and the shipped file against the code's defaults.
public sealed class SoundConfigTests
{
    private static readonly SoundConfig Config = SoundConfig.Load(Write("""
        {
            // comments are fine
            "server_record": ["sr"], "personal_best": ["pb"], "first_finish": ["first"], "worst": ["worst"],
            "worst_minimum": 5, "ranks": { "2": ["second"], "3": [] }
        }
        """), NullLogger.Instance);

    [Theory]
    [InlineData(true, false, 1, 10, "sr", true)]
    [InlineData(false, false, 2, 10, "second", true)]    // a rank sound plays to everyone
    [InlineData(false, false, 3, 10, "pb", false)]       // an empty rank falls through
    [InlineData(false, false, 10, 10, "worst", false)]
    [InlineData(false, false, 4, 4, "pb", false)]        // too few times for worst
    [InlineData(false, true, 7, 10, "first", false)]
    [InlineData(false, true, 0, 0, "first", false)]      // no rank known
    [InlineData(false, false, 5, 10, "pb", false)]
    public void ANewBestPlaysTheFirstSoundThatApplies(bool serverRecord, bool first, int rank, int total, string sound, bool everyone)
    {
        var (sounds, all) = Config.ForBest(serverRecord, first, rank, total);

        Assert.Equal((sound, everyone), (sounds.Single(), all));
    }

    [Fact]
    public void AnEmptyServerRecordFallsThrough()
    {
        var config = new SoundConfig { ServerRecord = [] };

        Assert.Equal((config.PersonalBest, false), config.ForBest(true, false, 1, 10));
    }

    [Fact]
    public void TheShippedConfigMatchesTheDefaults()
    {
        var root     = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(HudAssets.Locale()))))!;
        var shipped  = SoundConfig.Load(Path.Combine(root, "sharp", "configs", "timer-sounds.jsonc"), NullLogger.Instance);
        var defaults = new SoundConfig();

        Assert.Equal(defaults.ServerRecord, shipped.ServerRecord);
        Assert.Equal(defaults.PersonalBest, shipped.PersonalBest);
        Assert.Equal(defaults.FirstFinish, shipped.FirstFinish);
        Assert.Equal(defaults.NoImprovement, shipped.NoImprovement);
        Assert.Equal(defaults.Worst, shipped.Worst);
        Assert.Equal(defaults.WorstMinimum, shipped.WorstMinimum);
        Assert.Empty(shipped.Ranks);
        Assert.Empty(shipped.Precache);
    }

    private static string Write(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"timer-sounds-{Guid.NewGuid():N}.jsonc");
        File.WriteAllText(path, json);

        return path;
    }
}
