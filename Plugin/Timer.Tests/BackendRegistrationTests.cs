using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Source2Surf.Timer;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

public sealed class BackendRegistrationTests
{
    [Fact]
    public void RequestsAndReplayUrlsShareOneBackendClient()
    {
        using var provider = Build(new Dictionary<string, string?>
        {
            ["backend:endpoint"] = "http://backend.example:5082",
        });

        var requests = provider.GetRequiredService<IRequestManager>();

        Assert.IsType<BackendRequestManager>(requests);
        Assert.Same(requests, provider.GetRequiredService<IReplayCatalog>());
        Assert.Equal(new Uri("http://backend.example:5082"), provider.GetRequiredService<BackendOptions>().Endpoint);
    }

    [Fact]
    public void MissingEndpointResolvesTheLocalBackend()
    {
        using var provider = Build(new Dictionary<string, string?>());

        Assert.IsType<BackendRequestManager>(provider.GetRequiredService<IRequestManager>());
        Assert.Equal(new Uri(BackendOptions.DefaultEndpoint), provider.GetRequiredService<BackendOptions>().Endpoint);
    }

    private static ServiceProvider Build(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services      = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton((InterfaceBridge)RuntimeHelpers.GetUninitializedObject(typeof(InterfaceBridge)));
        services.AddManagerService();

        return services.BuildServiceProvider();
    }
}
