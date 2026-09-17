using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Timer.Backend.Configuration;
using Timer.Backend.WriteApi;
using Timer.RequestManager.Backend;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteApiRegistrationTests
{
    [Fact]
    public void DisabledWriteApiDoesNotRegisterWriteFacadeOrMagicOnionServices()
    {
        var services = new ServiceCollection();
        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());

        TimerWriteApiRegistration.Add(services, options);

        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(TimerBackendWriteStorage));
    }

    [Fact]
    public void EnabledWriteApiRegistersIndependentWriteFacade()
    {
        var services = new ServiceCollection();
        var options = EnabledOptions();

        TimerWriteApiRegistration.Add(services, options);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(TimerBackendWriteStorage));
    }

    [Fact]
    public async Task ServiceEndpointsAreMappedOnlyWhenTheWriteApiIsEnabled()
    {
        var disabled = await BuildAndMapAsync(TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build()));
        try
        {
            Assert.Empty(((IEndpointRouteBuilder)disabled).DataSources.SelectMany(source => source.Endpoints));
        }
        finally
        {
            await disabled.DisposeAsync();
        }

        var enabled = await BuildAndMapAsync(EnabledOptions());
        try
        {
            var endpoints = ((IEndpointRouteBuilder)enabled).DataSources.SelectMany(source => source.Endpoints).ToArray();
            Assert.NotEmpty(endpoints);
            Assert.Contains(endpoints, endpoint => endpoint.DisplayName?.Contains(nameof(TimerWriteServiceV1), StringComparison.Ordinal) == true);
        }
        finally
        {
            await enabled.DisposeAsync();
        }
    }

    private static Task<WebApplication> BuildAndMapAsync(TimerWriteApiOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(options);
        TimerWriteApiRegistration.Add(builder.Services, options);
        var application = builder.Build();
        TimerWriteApiRegistration.Map(application, options);
        return Task.FromResult(application);
    }

    private static TimerWriteApiOptions EnabledOptions()
    {
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
            ["TimerBackend:WriteApi:RulesetVersion"] = "7",
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
        };
        return TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }
}
