using Source2Surf.Timer.Shared.Models;
using Timer.MapChooser;
using Timer.MapChooser.Logic;
using Xunit;

namespace Timer.Tests;

public sealed class MapChooserTests
{
    private static MapProfile Profile(string name, ulong mapId, byte tier, ulong workshopId = 0)
    {
        var profile = new MapProfile { MapName = name, MapId = mapId, WorkshopId = workshopId };
        profile.Tier[0] = tier;

        return profile;
    }

    [Fact]
    public void EveryChooserTextIsInTheLocaleFileWithItsEnglish()
    {
        var locale = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HudAssets.Locale()))!;

        Assert.Equal(ChooserTexts.All.Count, ChooserTexts.All.Select(t => t.Key).Distinct().Count());

        foreach (var text in ChooserTexts.All)
        {
            Assert.True(locale.TryGetValue(text.Key, out var translations), text.Key);
            Assert.Equal(text.English, translations["en-us"]);
            Assert.True(translations.ContainsKey("zh-cn"), text.Key);
        }

        Assert.Empty(locale.Keys.Where(x => x.StartsWith("mc.", StringComparison.Ordinal)).Except(ChooserTexts.All.Select(t => t.Key)));
    }

    [Fact]
    public void PoolTakesTiersByWorkshopItemThenNameAndSortsByName()
    {
        // surf_b was renamed on the workshop, so only its item id still matches its row.
        var profiles = new[]
        {
            Profile("surf_b_old", 2, 4, 222),
            Profile("surf_a", 1, 3),
            Profile("surf_local", 5, 2),
        };

        var pool = MapPool.Build([(111, "surf_c"), (222, "surf_b"), (333, "SURF_A"), (444, "surf_skip")],
                                 ["surf_local", "surf_c"],
                                 profiles,
                                 ["surf_skip"]);

        Assert.Equal(["SURF_A", "surf_b", "surf_c", "surf_local"], pool.Select(x => x.Name));
        Assert.Equal(new PoolMap("SURF_A", 333, 1, 3, true), pool[0]);
        Assert.Equal(new PoolMap("surf_b", 222, 2, 4, true), pool[1]);
        Assert.Equal(new PoolMap("surf_c", 111, 0, 0, true), pool[2]);
        Assert.Equal(new PoolMap("surf_local", 0, 5, 2, false), pool[3]);
    }

    [Fact]
    public void NominationsKeepOnePerPlayerInOrder()
    {
        var nominations = new Nominations { Max = 2 };

        Assert.Equal(NominateResult.Nominated, nominations.Add(1, "surf_a"));
        Assert.Equal(NominateResult.AlreadyNominated, nominations.Add(2, "SURF_A"));
        Assert.Equal(NominateResult.Nominated, nominations.Add(2, "surf_b"));
        Assert.Equal(NominateResult.Full, nominations.Add(3, "surf_c"));
        Assert.Equal(NominateResult.Replaced, nominations.Add(1, "surf_c"));
        Assert.Equal(["surf_c", "surf_b"], nominations.Maps);
        Assert.Equal("surf_b", nominations.Remove(2));
        Assert.Null(nominations.Remove(2));
        Assert.Equal(NominateResult.Nominated, nominations.Add(3, "surf_b"));
        Assert.Equal("surf_c", nominations.Of(1));
    }

    [Theory]
    [InlineData(0, 0.6f, 1)]
    [InlineData(1, 0.6f, 1)]
    [InlineData(4, 0.6f, 3)]
    [InlineData(5, 0.6f, 3)]
    [InlineData(10, 0.5f, 5)]
    [InlineData(3, 1f, 3)]
    public void RockTheVoteNeedsTheShareRoundedUp(int players, float ratio, int needed)
        => Assert.Equal(needed, RockTheVote.Needed(players, ratio));

    [Fact]
    public void VoteCountsChangedVotesAndLeavers()
    {
        var vote = new MapVote(MapVoteKind.EndOfMap, [new ("surf_a", 1), new ("surf_b", 2), new ("", 0, 10)], 30);

        Assert.True(vote.Cast(1, 0));
        Assert.False(vote.Cast(1, 0));
        Assert.True(vote.Cast(2, 0));
        Assert.True(vote.Cast(1, 1));
        Assert.False(vote.Cast(3, 5));
        Assert.Equal([1, 1, 0], vote.Counts);

        Assert.True(vote.Remove(2));
        Assert.Equal([0, 1, 0], vote.Counts);
        Assert.Equal(1, vote.Choice(1));
        Assert.Equal(-1, vote.Choice(2));
        Assert.Equal(1, vote.Winner(new Random(1)));
    }

    [Fact]
    public void VoteTiesAreDrawnAndAnEmptyVotePicksAMap()
    {
        var options = new MapVoteOption[] { new ("surf_a", 1), new ("surf_b", 2), new ("surf_c", 3), new ("", 0, 10) };

        var tied = new MapVote(MapVoteKind.EndOfMap, options, 30);
        tied.Cast(1, 0);
        tied.Cast(2, 2);

        var empty = new MapVote(MapVoteKind.EndOfMap, options, 30);

        for (var seed = 0; seed < 50; seed++)
        {
            Assert.Contains(tied.Winner(new Random(seed)), new[] { 0, 2 });
            Assert.Contains(empty.Winner(new Random(seed)), new[] { 0, 1, 2 });
        }
    }

    [Fact]
    public void VoteHasNominationsFirstThenRandomMapsThenExtend()
    {
        var a = new PoolMap("surf_a", 1, 1, 1, true);
        var b = new PoolMap("surf_b", 2, 2, 2, true);
        var candidates = Enumerable.Range(0, 10).Select(i => new PoolMap($"surf_{i}", (ulong) i, 0, 0, true)).Append(b).ToList();

        var options = VoteBuilder.Build([b, a, b], candidates, 4, 10, new Random(3));

        Assert.Equal(5, options.Count);
        Assert.Equal(new MapVoteOption("surf_b", 2), options[0]);
        Assert.Equal(new MapVoteOption("surf_a", 1), options[1]);
        Assert.Equal(2, options.Skip(2).Take(2).Select(x => x.Map).Distinct().Count());
        Assert.DoesNotContain(options.Skip(2).Take(2), x => x.Map is "surf_a" or "surf_b");
        Assert.True(options[4].IsExtend);
        Assert.Equal(10, options[4].ExtendMinutes);

        var few = VoteBuilder.Build([], [a], 5, 0, new Random(3));
        Assert.Equal([new MapVoteOption("surf_a", 1)], few);
    }

    [Theory]
    [InlineData("3", 2)]
    [InlineData("!3", 2)]
    [InlineData("/1", 0)]
    [InlineData(".9", 8)]
    [InlineData("！5", 4)]
    [InlineData(" !2 ", 1)]
    [InlineData("0", -1)]
    [InlineData("!0", -1)]
    [InlineData("10", -1)]
    [InlineData("!10", -1)]
    [InlineData("a", -1)]
    [InlineData("!!", -1)]
    [InlineData("rtv", -1)]
    public void ChatVotesAreANumberWithOrWithoutAPrefix(string message, int option)
        => Assert.Equal(option, MapChooserModule.ChatVote(message));

    [Fact]
    public void ClockCountsDownAndOnlyVotedExtendsCount()
    {
        var clock = new MapClock();
        clock.Start(100, 30);

        Assert.Equal(1800, clock.TimeLeft(100));
        Assert.Equal(60, clock.Elapsed(160));

        clock.Extend(10);
        clock.Extend(5, false);

        Assert.Equal(1, clock.Extends);
        Assert.Equal(2700, clock.TimeLeft(100));
    }

    [Fact]
    public void RecentMapsKeepTheNewestFirst()
    {
        var recent = new RecentMaps(3, ["surf_c", "surf_b", "surf_a"]);

        Assert.Equal(["surf_c", "surf_b", "surf_a"], recent.Maps);

        recent.Push("SURF_A");
        recent.Push("surf_d");

        Assert.Equal(["surf_d", "SURF_A", "surf_c"], recent.Maps);
        Assert.True(recent.Contains("surf_C"));
        Assert.False(recent.Contains("surf_b"));
    }

    [Fact]
    public void ConfigIsWrittenWhenMissingAndClamped()
    {
        var dir  = Path.Combine(Path.GetTempPath(), $"timer-mapchooser-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "timer-mapchooser.jsonc");

        try
        {
            var written = MapChooserConfig.Load(path);

            Assert.True(File.Exists(path));
            Assert.Equal(30, written.TimeLimit);
            Assert.Equal(5, written.VoteMaps);
            Assert.Equal("autobuy", written.KeyUp);
            Assert.Equal(Sharp.Shared.Enums.UserCommandButtons.LookAtWeapon, written.SelectButton);
            Assert.Equal("lookatweapon", written.SelectCommand);

            File.WriteAllText(path, """
                                    {
                                        // comments and trailing commas are fine
                                        "time_limit": 2,
                                        "vote_maps": 50,
                                        "vote_before_end": 9999,
                                        "extra_maps": ["surf_x"],
                                        "key_select": "nope",
                                    }
                                    """);

            var config = MapChooserConfig.Load(path);

            Assert.Equal(8, config.VoteMaps);
            Assert.Equal(120, config.VoteBeforeEnd);
            Assert.Equal(["surf_x"], config.ExtraMaps);
            Assert.Equal(Sharp.Shared.Enums.UserCommandButtons.LookAtWeapon, config.SelectButton);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
