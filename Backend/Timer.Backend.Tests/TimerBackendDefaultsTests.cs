using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Timer.Backend.Configuration;
using Xunit;

namespace Timer.Backend.Tests;

// The listeners a backend gets when its configuration names none.
public sealed class TimerBackendDefaultsTests
{
    [Fact]
    public void WithoutListenersTheBackendServesLoopback5081And5082()
    {
        var configuration = Apply([]);

        Assert.Equal(TimerBackendDefaults.HttpUrl, configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.Equal("Http1", configuration["Kestrel:Endpoints:Http:Protocols"]);
        Assert.Equal(TimerBackendDefaults.GrpcUrl, configuration["Kestrel:Endpoints:Grpc:Url"]);
        Assert.Equal("Http2", configuration["Kestrel:Endpoints:Grpc:Protocols"]);
        Assert.Equal([5082], TimerWriteApiOptions.FromConfiguration(configuration).LocalPorts);
    }

    [Fact]
    public void ConfiguredListenersAreLeftAlone()
    {
        var configuration = Apply(new() { ["Kestrel:Endpoints:Grpc:Url"] = "http://10.0.0.2:6000" });

        Assert.Equal("http://10.0.0.2:6000", configuration["Kestrel:Endpoints:Grpc:Url"]);
        Assert.Null(configuration["Kestrel:Endpoints:Http:Url"]);
        Assert.Empty(TimerWriteApiOptions.FromConfiguration(configuration).LocalPorts);
    }

    [Fact]
    public void AnUrlsSettingIsLeftAlone()
        => Assert.Null(Apply(new() { ["urls"] = "http://0.0.0.0:7000" })["Kestrel:Endpoints:Grpc:Url"]);

    [Fact]
    public void ConfiguredLocalPortsAreKept()
    {
        var configuration = Apply(new() { ["TimerBackend:WriteApi:LocalPorts"] = "6000" });

        Assert.Equal([6000], TimerWriteApiOptions.FromConfiguration(configuration).LocalPorts);
        Assert.Equal(TimerBackendDefaults.GrpcUrl, configuration["Kestrel:Endpoints:Grpc:Url"]);
    }

    private static ConfigurationManager Apply(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(values);
        TimerBackendDefaults.Apply(configuration);

        return configuration;
    }
}
