using Sharp.Shared.Definition;
using Source2Surf.Timer.Shared;
using Xunit;

namespace Timer.Tests;

// {colour} tags in chat texts and the chat prefix, which go through string.Format after.
public sealed class ChatColorTagTests
{
    [Theory]
    [InlineData("{green}PB{white}: {0}", ChatColor.Green + "PB" + ChatColor.White + ": {0}")]
    [InlineData("{LIME}x", ChatColor.Lime + "x")]
    [InlineData("{{0}} {nope} {", "{{0}} {nope} {")]
    [InlineData("no tags", "no tags")]
    public void KnownTagsBecomeColoursAndTheRestStays(string text, string expected)
        => Assert.Equal(expected, ChatColorTags.Apply(text));

    [Theory]
    [InlineData("CP {0} | {red}{1}", 1, "16.171" + ChatColor.Grey)]
    [InlineData("CP {0} | {red}{1:0}", 1, "16.171" + ChatColor.Grey)]
    [InlineData("CP {red}{0} | {1}", 1, ChatColor.Gold + "16.171" + ChatColor.Grey)]
    [InlineData("CP {0} | {1}", 1, ChatColor.Gold + "16.171" + ChatColor.Grey)]
    [InlineData("CP {red} {1}", 1, ChatColor.Gold + "16.171" + ChatColor.Grey)]
    [InlineData("{red}{11}", 1, ChatColor.Gold + "16.171" + ChatColor.Grey)]
    public void ATagRightBeforeAPlaceholderDropsItsValuesOwnColour(string text, int index, string expected)
    {
        var template = ChatColorTags.Apply(text);
        var value    = ChatColorTags.Recolor(template, index, ChatColor.Gold + "16.171" + ChatColor.Grey);

        Assert.Equal(expected, value);
    }

    [Fact]
    public void UncolouredAndNonStringValuesStayAsTheyAre()
    {
        var template = ChatColorTags.Apply("{red}{0} {red}{1}");

        Assert.Equal("16.171", ChatColorTags.Recolor(template, 0, "16.171"));
        Assert.Equal(42, ChatColorTags.Recolor(template, 1, 42));
    }

    [Fact]
    public void ThePrefixComesFromTimerJsoncsChatSection()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """
                                    {
                                      // a comment
                                      "chat": { "prefix": "{red}Surf{white} > " },
                                    }
                                    """);

            Assert.Equal(" " + ChatColor.Red + "Surf" + ChatColor.White + " > ", ChatColorTags.LoadPrefix(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ \"chat\": {} }")]
    [InlineData("not json")]
    public void WithoutOneThePrefixIsTheDefault(string json)
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, json);

            Assert.Equal(" " + ChatColor.Lime + "Timer" + ChatColor.White + " | ", ChatColorTags.LoadPrefix(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
