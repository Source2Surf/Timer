using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Submission;
using Xunit;

namespace Timer.Tests;

public sealed class SenderModeRegistrationTests
{
    [Fact]
    public void RemoteModeNeedsOnlyAnEndpointToEnableTheSender()
    {
        var values = new Dictionary<string, string?>
        {
            ["Timer:ScoreWrite:Mode"] = "remote-write",
            ["Timer:RunSubmissionSender:Endpoint"] = "http://backend.example:5082",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(ScoreWriteModeOptions.FromConfiguration(configuration));
        services.AddManagerService();
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<RunSubmissionSenderOptions>();
        Assert.True(options.Enabled);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData("local-sql", true, true)]
    [InlineData("remote-write", false, true)]
    [InlineData("remote-write", true, false)]
    public void SenderCannotRunOutsideExplicitRemoteMode(string? mode,
                                                         bool senderEnabled,
                                                         bool shouldFail)
    {
        var values = new Dictionary<string, string?>
        {
            ["Timer:ScoreWrite:Mode"] = mode,
            ["Timer:RunSubmissionSender:Enabled"] = senderEnabled.ToString(),
            ["Timer:RunSubmissionSender:Endpoint"] = "https://backend.example:443",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(ScoreWriteModeOptions.FromConfiguration(configuration));
        services.AddManagerService();
        using var provider = services.BuildServiceProvider();

        if (shouldFail)
        {
            Assert.Throws<InvalidOperationException>(() =>
                provider.GetRequiredService<RunSubmissionSenderOptions>());
        }
        else
        {
            Assert.Equal(senderEnabled,
                         provider.GetRequiredService<RunSubmissionSenderOptions>().Enabled);
        }
    }
}
