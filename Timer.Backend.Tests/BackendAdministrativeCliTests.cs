using System;
using Timer.Backend.Administration;
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
    public void OrdinaryServerArgumentsRemainUntouched()
    {
        var invocation = BackendAdministrativeCli.Parse(["--urls", "http://127.0.0.1:5081"]);

        Assert.Equal(BackendAdministrativeOperation.Serve, invocation.Operation);
        Assert.Equal(["--urls", "http://127.0.0.1:5081"], invocation.ConfigurationArguments);
    }
}
