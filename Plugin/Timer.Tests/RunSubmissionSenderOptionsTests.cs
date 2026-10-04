using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Managers.Submission;
using Xunit;

namespace Timer.Tests;

public sealed class RunSubmissionSenderOptionsTests
{
    [Fact]
    public void MissingSectionIsDisabledWithoutEndpoint()
    {
        var options = RunSubmissionSenderOptions.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.False(options.Enabled);
        Assert.Null(options.Endpoint);
    }

    [Fact]
    public void EndpointAloneEnablesSenderWithoutHttps()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunSubmissionSenderOptions.FromConfiguration(Build(("enabled", "true"))));

        var options = RunSubmissionSenderOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082")));

        Assert.True(options.Enabled);
        Assert.Equal(new Uri("http://timer.example:5082/"), options.Endpoint);
    }

    [Fact]
    public void HttpsEndpointIsAcceptedWithoutCredentials()
    {
        var options = RunSubmissionSenderOptions.FromConfiguration(Build(("enabled", "true"),
                                                                          ("endpoint", "https://timer.example")));
        Assert.Equal(Uri.UriSchemeHttps, options.Endpoint?.Scheme);
    }

    [Fact]
    public void ExplicitDisabledSenderStaysDisabledAndLegacyH2cSwitchIsAccepted()
    {
        var options = RunSubmissionSenderOptions.FromConfiguration(Build(("enabled", "false"),
                                                                          ("endpoint", "http://timer.example:5082"),
                                                                          ("allow_insecure_loopback", "true")));

        Assert.False(options.Enabled);
        Assert.True(options.AllowInsecureLoopback);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("server_id")]
    public void ObsoleteIdentitySettingsFailFast(string setting)
    {
        Assert.Throws<InvalidOperationException>(() =>
            RunSubmissionSenderOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082"),
                                                               (setting, "obsolete"))));
    }

    private static IConfiguration Build(params (string Name, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (name, value) in settings)
        {
            values[$"{RunSubmissionSenderOptions.SectionName}:{name}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
