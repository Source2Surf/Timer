using System.IO;
using System.Linq;
using System.Text.Json;
using Sharp.Shared.Definition;
using Source2Surf.Timer;
using Source2Surf.Timer.Modules.Rank;
using Xunit;

namespace Timer.Tests;

// Titles by global points rank, and the shipped timer-ranks.jsonc against the code's defaults.
public sealed class RankTitleTests
{
    [Theory]
    [InlineData(1, "Legend")]
    [InlineData(10, "Legend")]
    [InlineData(11, "Master")]
    [InlineData(150, "Elite")]
    [InlineData(600, "Skilled")]
    [InlineData(601, "Ranked")]
    [InlineData(100000, "Ranked")]
    public void DefaultTitlesFollowTheRank(int rank, string title)
        => Assert.Equal(title, new RankTitles(new RankConfig()).For(rank)?.Name);

    [Fact]
    public void UnrankedHasNoTitleUnlessConfigured()
    {
        Assert.Null(new RankTitles(new RankConfig()).For(0));
        Assert.Equal("New", new RankTitles(new RankConfig { Unranked = "New" }).For(0)?.Name);
    }

    [Fact]
    public void TitlesAreSortedNamelessOnesSkippedAndColorsResolved()
    {
        var titles = new RankTitles(new RankConfig
        {
            Titles =
            [
                new () { Name = "Rest", MaxRank = 0, Color = "nope" },
                new () { Name = "", MaxRank = 1 },
                new () { Name = "Top", MaxRank = 5, Color = "gold" },
            ],
        });

        Assert.Equal(new RankTitle("Top", ChatColor.Gold), titles.For(1));
        Assert.Equal(new RankTitle("Rest", ChatColor.White), titles.For(6));
    }

    [Fact]
    public void TheShippedConfigMatchesTheDefaults()
    {
        var root    = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(HudAssets.Locale()))))!;
        var shipped = JsonSerializer.Deserialize<RankConfig>(File.ReadAllText(Path.Combine(root, "sharp", "configs", "timer-ranks.jsonc")),
                                                             Utils.DeserializerOptions)!;
        var defaults = new RankConfig();

        Assert.Equal(defaults.Titles.Select(t => (t.Name, t.MaxRank, t.Color)), shipped.Titles.Select(t => (t.Name, t.MaxRank, t.Color)));
        Assert.Equal((defaults.Unranked, defaults.ChatTags, defaults.ScoreboardTags), (shipped.Unranked, shipped.ChatTags, shipped.ScoreboardTags));
        Assert.Equal((defaults.ChatFormat, defaults.ChatFormatUntitled, defaults.ScoreboardFormat),
                     (shipped.ChatFormat, shipped.ChatFormatUntitled, shipped.ScoreboardFormat));
        Assert.Equal((defaults.ChatPrefixDead, defaults.ChatPrefixSpec, defaults.ChatPrefixTeam),
                     (shipped.ChatPrefixDead, shipped.ChatPrefixSpec, shipped.ChatPrefixTeam));
    }

    [Fact]
    public void FormatsFillTheirTokens()
    {
        var master = new RankTitle("Master", ChatColor.LightRed);

        Assert.Equal($"*DEAD* {ChatColor.LightRed}[Master] {ChatColor.Head}Nuko{ChatColor.White}: gg",
                     RankTitles.Render(new RankConfig().ChatFormat, master, 12, 300, "Nuko", "gg", "*DEAD* "));
        Assert.Equal($"{ChatColor.Head}Nuko{ChatColor.White}: gg", RankTitles.Render(new RankConfig().ChatFormatUntitled, null, 0, 0, "Nuko", "gg"));
        Assert.Equal("#12 Master", RankTitles.Render("#{rank} {title}", master, 12, 300));
        Assert.Equal($"{ChatColor.Gold}#- of 300 {{oops}} {{", RankTitles.Render("{GOLD}#{rank} of {total} {oops} {", master, 0, 300));
    }
}
