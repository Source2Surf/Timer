using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Configuration;
using Xunit;

namespace Timer.Tests;

public sealed class BackendOptionsTests
{
    [Fact]
    public void MissingEndpointFailsStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            BackendOptions.FromConfiguration(new ConfigurationBuilder().Build()));

        Assert.Contains("backend:endpoint", error.Message);
    }

    [Fact]
    public void LeftoverScoreWriteSectionNamesItsReplacement()
    {
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["score_write:mode"]     = "remote-write",
                                ["score_write:endpoint"] = "http://timer.example:5082",
                            })
                            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => BackendOptions.FromConfiguration(configuration));

        Assert.Contains("score_write", error.Message);
    }

    [Fact]
    public void EndpointAloneIsEnoughWithoutHttps()
    {
        var options = BackendOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082")));

        Assert.Equal(new Uri("http://timer.example:5082/"), options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RpcDeadline);
    }

    [Fact]
    public void HttpsEndpointIsAcceptedWithoutCredentials()
    {
        var options = BackendOptions.FromConfiguration(Build(("endpoint", "https://timer.example")));

        Assert.Equal(Uri.UriSchemeHttps, options.Endpoint.Scheme);
    }

    [Theory]
    [InlineData("api_key")]
    [InlineData("server_id")]
    [InlineData("mode")]
    [InlineData("enabled")]
    public void UnsupportedSettingsFailFast(string setting)
    {
        Assert.Throws<InvalidOperationException>(() =>
            BackendOptions.FromConfiguration(Build(("endpoint", "http://timer.example:5082"),
                                                   (setting, "obsolete"))));
    }

    private static IConfiguration Build(params (string Name, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (name, value) in settings)
        {
            values[$"{BackendOptions.SectionName}:{name}"] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
