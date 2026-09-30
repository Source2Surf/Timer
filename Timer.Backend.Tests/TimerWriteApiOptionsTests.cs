using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Timer.Backend.Configuration;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteApiOptionsTests
{
    [Fact]
    public void MissingSectionDefaultsToDisabledAndStyleZero()
    {
        var options = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.False(options.Enabled);
        Assert.Equal(0, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
    }

    [Fact]
    public void EnabledApiDefaultsToRulesetOneAndMainStyleFactorOne()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
        }));
        Assert.True(options.Enabled);
        Assert.Equal(1, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
    }

    [Fact]
    public void ValidConfigurationParsesStyleFactors()
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:StyleFactors:15"] = "100";
        var options = TimerWriteApiOptions.FromConfiguration(Build(values));
        Assert.True(options.Enabled);
        Assert.Equal(7, options.RulesetVersion);
        Assert.Equal(1d, options.StyleFactors[0]);
        Assert.Equal(100d, options.StyleFactors[15]);
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("Servers:alpha:ApiKeySha256")]
    public void ObsoleteIdentityConfigurationIsRejected(string setting)
    {
        var values = EnabledValues();
        values[$"TimerBackend:WriteApi:{setting}"] = "obsolete";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void UppercaseEnvironmentStyleSettingNamesRemainAccepted()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(new()
        {
            ["TIMERBACKEND:WRITEAPI:ENABLED"] = "true",
            ["TIMERBACKEND:WRITEAPI:RULESETVERSION"] = "7",
            ["TIMERBACKEND:WRITEAPI:STYLEFACTORS:0"] = "1",
        }));
        Assert.True(options.Enabled);
        Assert.Equal(1d, options.StyleFactors[0]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("16")]
    public void StyleOutsideRangeFailsFast(string style)
    {
        var values = EnabledValues();
        values[$"TimerBackend:WriteApi:StyleFactors:{style}"] = "1";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("100.1")]
    [InlineData("-0.1")]
    public void NonFiniteOrOutOfRangeStyleFactorFailsFast(string factor)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:StyleFactors:0"] = factor;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ExplicitNonPositiveRulesetStillFailsWhenEnabled()
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:RulesetVersion"] = "0";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void EnabledApiRequiresStyleZeroWhenCustomFactorsAreConfigured()
    {
        var values = EnabledValues();
        values.Remove("TimerBackend:WriteApi:StyleFactors:0");
        values["TimerBackend:WriteApi:StyleFactors:1"] = "1";
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Fact]
    public void ImplicitStyleZeroDefaultIsNotReportedAsExplicit()
    {
        var implicitOptions = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());
        var explicitOptions = TimerWriteApiOptions.FromConfiguration(Build(EnabledValues()));

        Assert.False(implicitOptions.HasExplicitStyleFactors);
        Assert.True(explicitOptions.HasExplicitStyleFactors);
    }

    [Fact]
    public void LocalPortsDefaultToEveryListener()
    {
        var options = TimerWriteApiOptions.FromConfiguration(Build(EnabledValues()));

        Assert.Empty(options.LocalPorts);
    }

    [Fact]
    public void LocalPortsAcceptArraysAndScalars()
    {
        var arrayValues = EnabledValues();
        arrayValues["TimerBackend:WriteApi:LocalPorts:0"] = "5082";
        arrayValues["TimerBackend:WriteApi:LocalPorts:1"] = "5083";
        var scalarValues = EnabledValues();
        scalarValues["TimerBackend:WriteApi:LocalPorts"] = "5082";

        Assert.Equal(new[] { 5082, 5083 }, TimerWriteApiOptions.FromConfiguration(Build(arrayValues)).LocalPorts.Order());
        Assert.Equal(new[] { 5082 }, TimerWriteApiOptions.FromConfiguration(Build(scalarValues)).LocalPorts);
    }

    [Fact]
    public void LocalPortsSetAsBothScalarAndListFailsFast()
    {
        // An env scalar layered over an appsettings array would otherwise merge into both ports.
        var values = EnabledValues();
        values["TimerBackend:WriteApi:LocalPorts:0"] = "5082";
        values["TimerBackend:WriteApi:LocalPorts"] = "6000";

        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("grpc")]
    public void InvalidLocalPortFailsFast(string port)
    {
        var values = EnabledValues();
        values["TimerBackend:WriteApi:LocalPorts:0"] = port;
        Assert.Throws<InvalidOperationException>(() => TimerWriteApiOptions.FromConfiguration(Build(values)));
    }

    private static Dictionary<string, string?> EnabledValues() => new()
    {
        ["TimerBackend:WriteApi:Enabled"] = "true",
        ["TimerBackend:WriteApi:RulesetVersion"] = "7",
        ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
    };

    private static IConfiguration Build(Dictionary<string, string?> values)
        => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
