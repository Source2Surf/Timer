using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Timer.RequestManager;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class SchemaInitializationConfigTests
{
    [Fact]
    public void FreshInstallKeepsLegacyBootstrapDefault()
    {
        Assert.True(SqlRequestManager.ResolveInitializeSchema(null, new ConfigurationBuilder().Build()));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void ConfigurationCanDisableOrEnablePluginCodeFirst(string configured, bool expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Timer:InitializeSchema"] = configured,
            }).Build();

        Assert.Equal(expected, SqlRequestManager.ResolveInitializeSchema(null, configuration));
    }

    [Fact]
    public void TimerJsoncDatabaseFlagOverridesConfiguration()
    {
        using var timerJsonc = JsonDocument.Parse("""
            { "database": { "initialize_schema": false } }
            """);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Timer:InitializeSchema"] = "true",
            }).Build();

        Assert.False(SqlRequestManager.ResolveInitializeSchema(timerJsonc, configuration));
    }

    [Fact]
    public void InvalidPluginSchemaFlagFailsFast()
    {
        using var timerJsonc = JsonDocument.Parse("""
            { "database": { "initialize_schema": 1 } }
            """);

        Assert.Throws<InvalidDataException>(() =>
            SqlRequestManager.ResolveInitializeSchema(timerJsonc, new ConfigurationBuilder().Build()));
    }
}
