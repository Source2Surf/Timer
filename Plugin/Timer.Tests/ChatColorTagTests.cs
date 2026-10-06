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
