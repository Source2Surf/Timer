using System;
using Microsoft.Extensions.Logging;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerBackendStorageLifecycleTests
{
    [Theory]
    [InlineData("mysql", "Server=127.0.0.1;Database=timer;User ID=timer")]
    [InlineData("postgresql", "Host=127.0.0.1;Database=timer;Username=timer")]
    public void SchemaSkippedStartupStillChecksRunDateCompatibilityBeforeServing(string databaseType, string connectionString)
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });
        using var storage = TimerBackendStorageFactory.Create(databaseType, connectionString, loggerFactory);

        // Dummy credentials cannot connect. Skipping DDL must not also skip the legacy-Date
        // preflight, or the new BIGINT mapping could serve against a temporal master column.
        Assert.ThrowsAny<Exception>(() => storage.Start(initializeSchema: false, allowReadRepair: false));
        Assert.Throws<ObjectDisposedException>(() => storage.Start(false, false));
    }

    [Fact]
    public void UnsupportedProviderFailsBeforeConstructingStorage()
    {
        using var loggerFactory = LoggerFactory.Create(static _ => { });

        Assert.Throws<NotSupportedException>(() =>
            TimerBackendStorageFactory.Create("sqlite", "Data Source=timer.db", loggerFactory));
    }
}
