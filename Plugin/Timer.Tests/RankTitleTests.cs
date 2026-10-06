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
    [InlineData(1, 10000, "Champion")]
    [InlineData(2, 10000, "Legend")]
    [InlineData(3, 10000, "Legend")]
    [InlineData(4, 10000, "Master")]
    [InlineData(10, 10000, "Master")]
    [InlineData(11, 10000, "Elite")]
    [InlineData(100, 10000, "Elite")]
    [InlineData(101, 10000, "Expert")]
    [InlineData(500, 10000, "Expert")]
    [InlineData(501, 10000, "Skilled")]
    [InlineData(2000, 10000, "Skilled")]
    [InlineData(2001, 10000, "Ranked")]
    [InlineData(11, 40, "Ranked")]
    public void DefaultTitlesFollowTheRank(int rank, int total, string title)
        => Assert.Equal(title, new RankTitles(new RankConfig()).For(rank, total)?.Name);

    [Fact]
    public void UnrankedHasNoTitleUnlessConfigured()
    {
        Assert.Null(new RankTitles(new RankConfig()).For(0, 100));
        Assert.Equal("New", new RankTitles(new RankConfig { Unranked = "New" }).For(0, 100)?.Name);
    }

    [Fact]
    public void TitlesGoTopDownNamelessOnesSkippedAndColorsResolved()
    {
        var titles = new RankTitles(new RankConfig
        {
            Titles =
            [
                new () { Name = "", MaxRank = 1 },
                new () { Name = "Top", MaxRank = 5, Color = "gold" },
                new () { Name = "Rest", MaxRank = 0, Color = "nope" },
                new () { Name = "Never", MaxRank = 2 },
            ],
        });

        Assert.Equal(new RankTitle("Top", ChatColor.Gold), titles.For(1, 100));
        Assert.Equal(new RankTitle("Rest", ChatColor.White), titles.For(6, 100));
    }

    // A small server's 1% can be narrower than the top 10; Legend still comes first.
    [Theory]
    [InlineData(1, 500, "Legend")]
    [InlineData(10, 500, "Legend")]
    [InlineData(11, 500, "Elite")]
    [InlineData(11, 2000, "Master")]
    [InlineData(20, 2000, "Master")]
    [InlineData(21, 2000, "Elite")]
    [InlineData(100, 2000, "Elite")]
    [InlineData(101, 2000, "Ranked")]
    [InlineData(11, 50, "Ranked")]
    [InlineData(200, 20000, "Master")]
    [InlineData(201, 20000, "Elite")]
    public void PercentTitlesFollowTheRankedPlayerCount(int rank, int total, string title)
    {
        var titles = new RankTitles(new RankConfig
        {
            Titles =
            [
                new () { Name = "Legend", MaxRank    = 10 },
                new () { Name = "Master", MaxPercent = 1 },
                new () { Name = "Elite", MaxPercent  = 5 },
                new () { Name = "Ranked" },
            ],
        });

        Assert.Equal(title, titles.For(rank, total)?.Name);
    }

    [Fact]
    public void MaxRankWinsOverMaxPercent()
    {
        var titles = new RankTitles(new RankConfig
        {
            Titles =
            [
                new () { Name = "Both", MaxRank    = 3, MaxPercent = 50 },
                new () { Name = "Half", MaxPercent = 50 },
            ],
        });

        Assert.Equal("Both", titles.For(3, 100)?.Name);
        Assert.Equal("Half", titles.For(4, 100)?.Name);
        Assert.Equal("Half", titles.For(50, 100)?.Name);
        Assert.Null(titles.For(51, 100));
    }

    [Fact]
    public void TheShippedConfigMatchesTheDefaults()
    {
        var root    = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(HudAssets.Locale()))))!;
        var shipped = JsonSerializer.Deserialize<RankConfig>(File.ReadAllText(Path.Combine(root, "sharp", "configs", "timer-ranks.jsonc")),
                                                             Utils.DeserializerOptions)!;
        var defaults = new RankConfig();

        Assert.Equal(defaults.Titles.Select(t => (t.Name, t.MaxRank, t.MaxPercent, t.Color)),
                     shipped.Titles.Select(t => (t.Name, t.MaxRank, t.MaxPercent, t.Color)));
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
