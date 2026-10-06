using System.Reflection;
using Source2Surf.Timer;
using Source2Surf.Timer.Shared.Interfaces;
using Xunit;

namespace Timer.Tests;

public sealed class JitWarmupTests
{
    private static readonly Assembly[] PluginAssemblies =
        [typeof(Source2Surf.Timer.Timer).Assembly, typeof(IRequestManager).Assembly];

    [Fact]
    public void CompilesThePluginWithoutRunningOrFailingAnyMethod()
    {
        var result = JitWarmup.Run(PluginAssemblies, CancellationToken.None);

        // The test host lacks the plugin's private dependencies (Microsoft.Extensions.Logging, ...),
        // which ModSharp loads from the module folder. Any other failure is a real one.
        var failed = result.Failed.Where(f => !f.Contains(nameof(FileNotFoundException), StringComparison.Ordinal)).ToList();

        Assert.False(result.Cancelled);
        Assert.True(result.Prepared > 1000, $"only {result.Prepared} methods were compiled");
        Assert.True(failed.Count == 0, string.Join(Environment.NewLine, failed));
    }

    [Fact]
    public void StopsBeforeCompilingWhenCancelled()
    {
        var result = JitWarmup.Run(PluginAssemblies, new CancellationToken(canceled: true));

        Assert.True(result.Cancelled);
        Assert.Equal(0, result.Prepared);
    }
}
