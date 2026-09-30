using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Timer.Backend.Administration;
using Timer.Backend.Configuration;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class BackendAdministrativeCliTests
{
    [Fact]
    public void SetTierSeparatesItsArgumentsFromConfigurationSwitches()
    {
        var invocation = BackendAdministrativeCli.Parse(
            ["set-tier", "surf_kitsune", "3", "--TimerBackend:Database:Type=postgresql"]);

        Assert.Equal(BackendAdministrativeOperation.SetTier, invocation.Operation);
        Assert.Equal("surf_kitsune", invocation.MapName);
        Assert.Equal((byte)3, invocation.Tier);
        Assert.Equal(["--TimerBackend:Database:Type=postgresql"], invocation.ConfigurationArguments);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("256")]
    [InlineData("not-a-tier")]
    public void SetTierRejectsInvalidTierSyntax(string tier)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            BackendAdministrativeCli.Parse(["set-tier", "surf_kitsune", tier]));

        Assert.Contains("tier", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecalculateAllIsAOneShotAdministrativeInvocation()
    {
        var invocation = BackendAdministrativeCli.Parse(
            ["recalc-scores", "ALL", "--TimerBackend:WriteApi:StyleFactors:0=0"]);

        Assert.Equal(BackendAdministrativeOperation.RecalculateScores, invocation.Operation);
        Assert.Equal("ALL", invocation.MapName);
        Assert.Equal(["--TimerBackend:WriteApi:StyleFactors:0=0"], invocation.ConfigurationArguments);
    }

    [Fact]
    public void ConvertRunDatesRequiresAndStripsItsExplicitBackupConfirmation()
    {
        var invocation = BackendAdministrativeCli.Parse(
            ["convert-run-dates", "--backup-confirmed", "--TimerBackend:Database:Type=mysql"]);

        Assert.Equal(BackendAdministrativeOperation.ConvertRunDates, invocation.Operation);
        Assert.True(invocation.BackupConfirmed);
        Assert.Equal(["--TimerBackend:Database:Type=mysql"], invocation.ConfigurationArguments);
        Assert.Throws<ArgumentException>(() => BackendAdministrativeCli.Parse(["convert-run-dates"]));
    }

    [Fact]
    public void ScoreAdministrationRejectsTheImplicitStyleZeroDefault()
    {
        var implicitPolicy = TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().Build());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            BackendAdministrativeCli.EnsureExplicitScorePolicy(implicitPolicy));

        Assert.Contains("StyleFactors", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScoreAdministrationRequiresStyleZeroEvenWhenTheWriteApiIsDisabled()
    {
        var withoutStyleZero = BuildWriteOptions(new()
        {
            ["TimerBackend:WriteApi:StyleFactors:1"] = "0.5",
        });

        Assert.Throws<InvalidOperationException>(() =>
            BackendAdministrativeCli.EnsureExplicitScorePolicy(withoutStyleZero));
    }

    [Fact]
    public void ScoreAdministrationAcceptsAnExplicitPolicy()
    {
        var explicitPolicy = BuildWriteOptions(new()
        {
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1",
            ["TimerBackend:WriteApi:StyleFactors:1"] = "0.5",
        });

        BackendAdministrativeCli.EnsureExplicitScorePolicy(explicitPolicy);
    }

    [Fact]
    public void CompletionListsMapsThatWereNotRequeued()
    {
        var invocation = BackendAdministrativeCli.Parse(["recalc-scores", "all"]);
        var result = new Timer.RequestManager.Backend.TimerBackendScoreAdministrationResult
        {
            MapFound = true,
            MapsAffected = 1,
            FailedMaps = ["surf_bad: Score factor 1 for style 0 on track 0 of map 'surf_bad' (tier 27) would produce an unrepresentable score."],
        };

        var completion = BackendAdministrativeCli.FormatCompletion(invocation, result);

        Assert.Contains("1 map(s) were NOT requeued", completion, StringComparison.Ordinal);
        Assert.Contains("surf_bad", completion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("recalc-score", "all")]                               // typo of a command
    [InlineData("--environment", "Production", "recalc-scores", "all")] // command after a switch
    [InlineData("set-tier", "surf_x", "3", "4")]                       // extra positional argument
    [InlineData("migrate", "now")]
    public void ArgumentsThatWouldBeSilentlyDroppedAreRejected(params string[] args)
    {
        // The configuration provider ignores bare words, so these would otherwise start the server.
        Assert.Throws<ArgumentException>(() => BackendAdministrativeCli.Parse(args));
    }

    [Fact]
    public void ConfigurationSwitchFormsAreAccepted()
    {
        var invocation = BackendAdministrativeCli.Parse(
            ["--urls", "http://127.0.0.1:5081", "--TimerBackend:Database:Type=mysql", "/environment", "Production", "Logging:LogLevel:Default=Warning"]);

        Assert.Equal(BackendAdministrativeOperation.Serve, invocation.Operation);
    }

    [Fact]
    public void OrdinaryServerArgumentsRemainUntouched()
    {
        var invocation = BackendAdministrativeCli.Parse(["--urls", "http://127.0.0.1:5081"]);

        Assert.Equal(BackendAdministrativeOperation.Serve, invocation.Operation);
        Assert.Equal(["--urls", "http://127.0.0.1:5081"], invocation.ConfigurationArguments);
    }

    private static TimerWriteApiOptions BuildWriteOptions(Dictionary<string, string?> values)
        => TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
