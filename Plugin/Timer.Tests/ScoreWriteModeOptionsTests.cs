using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Configuration;
using Xunit;

namespace Timer.Tests;

public sealed class ScoreWriteModeOptionsTests
{
    [Fact]
    public void MissingModeKeepsExistingLocalSqlBehavior()
    {
        var configuration = new ConfigurationBuilder().Build();
        Assert.Equal(ScoreWriteMode.LocalSql,
                     ScoreWriteModeOptions.FromConfiguration(configuration).Mode);
    }

    [Fact]
    public void RemoteWriteRequiresAnExplicitModeSelection()
    {
        var configuration = Build("remote-write");
        Assert.Equal(ScoreWriteMode.RemoteWrite,
                     ScoreWriteModeOptions.FromConfiguration(configuration).Mode);
    }

    [Fact]
    public void UnknownModeFailsStartupRatherThanChoosingFallback()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ScoreWriteModeOptions.FromConfiguration(Build("automatic")));
    }

    private static IConfiguration Build(string mode)
        => new ConfigurationBuilder()
           .AddInMemoryCollection(new Dictionary<string, string?>
           {
               ["Timer:ScoreWrite:Mode"] = mode,
           })
           .Build();
}
