using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Timer.Backend.Configuration;
using Timer.Backend.Infrastructure;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class BackendConfigurationTests
{
    [Fact]
    public void SchemaIsCreatedByDefaultOtherMutatingOptionsAreOff()
    {
        var options = TimerBackendOptions.FromConfiguration(CreateConfiguration());

        Assert.True(options.InitializeSchema);
        Assert.False(options.AllowReadRepair);
        Assert.False(options.EnableOutboxWorker);
    }

    [Theory]
    [InlineData("TimerBackend:InitializeSchema")]
    [InlineData("TimerBackend:AllowReadRepair")]
    [InlineData("TimerBackend:EnableOutboxWorker")]
    public void InvalidBooleanOptionFailsFast(string key)
    {
        var values = ValidValues();
        values[key] = "sometimes";

        Assert.Throws<InvalidOperationException>(() =>
            TimerBackendOptions.FromConfiguration(new ConfigurationBuilder()
                                                  .AddInMemoryCollection(values)
                                                  .Build()));
    }

    [Fact]
    public void ConnectionStringsFallbackIsSupported()
    {
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:Database:Type"] = "mysql",
            ["ConnectionStrings:TimerBackend"] = "Server=localhost;Database=timer",
        };

        var options = TimerBackendOptions.FromConfiguration(new ConfigurationBuilder()
                                                             .AddInMemoryCollection(values)
                                                             .Build());

        Assert.Equal("Server=localhost;Database=timer", options.ConnectionString);
    }

    [Fact]
    public void HostShutdownBudgetCoversRequestDrainAndWorkerGracePeriod()
    {
        var services = new ServiceCollection();
        TimerBackendRuntimeRegistration.Add(services, new TimerBackendRuntimeOptions
        {
            RequestTimeout = TimeSpan.FromSeconds(15),
            WorkerShutdownTimeout = TimeSpan.FromSeconds(120),
        });

        using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<IOptions<HostOptions>>().Value;

        // The 30 s default would cut the configured 120 s worker grace period short.
        Assert.True(host.ShutdownTimeout >= TimeSpan.FromSeconds(135), host.ShutdownTimeout.ToString());
    }

    [Fact]
    public void ConnectionStringsFallbackAppliesWhenPrimaryValueIsBlank()
    {
        // Mirrors the shipped appsettings.json, which declares an empty primary value.
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:Database:Type"] = "mysql",
            ["TimerBackend:Database:ConnectionString"] = "",
            ["ConnectionStrings:TimerBackend"] = "Server=localhost;Database=timer",
        };

        var options = TimerBackendOptions.FromConfiguration(new ConfigurationBuilder()
                                                             .AddInMemoryCollection(values)
                                                             .Build());

        Assert.Equal("Server=localhost;Database=timer", options.ConnectionString);
    }

    [Fact]
    public void OutboxWorkerCanBeExplicitlyEnabled()
    {
        var values = ValidValues();
        values["TimerBackend:EnableOutboxWorker"] = "true";

        var options = TimerBackendOptions.FromConfiguration(new ConfigurationBuilder()
                                                             .AddInMemoryCollection(values)
                                                             .Build());

        Assert.True(options.EnableOutboxWorker);
    }

    [Fact]
    public void EnabledWriteApiAlwaysStartsBackendScoreWorker()
    {
        var defaultOptions = TimerBackendOptions.FromConfiguration(CreateConfiguration());
        Assert.False(defaultOptions.ShouldRunOutboxWorker(writeApiEnabled: false));
        Assert.True(defaultOptions.ShouldRunOutboxWorker(writeApiEnabled: true));

        var values = ValidValues();
        values["TimerBackend:EnableOutboxWorker"] = "true";
        var explicitWorker = TimerBackendOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Assert.True(explicitWorker.ShouldRunOutboxWorker(writeApiEnabled: false));
    }

    private static IConfiguration CreateConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(ValidValues()).Build();

    private static Dictionary<string, string?> ValidValues()
        => new ()
        {
            ["TimerBackend:Database:Type"] = "postgresql",
            ["TimerBackend:Database:ConnectionString"] = "Host=localhost;Database=timer",
        };
}
