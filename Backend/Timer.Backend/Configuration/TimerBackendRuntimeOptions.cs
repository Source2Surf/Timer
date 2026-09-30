using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Timer.Backend.Configuration;

internal sealed record TimerBackendRuntimeOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WorkerShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WorkerMaxLag { get; init; } = TimeSpan.FromMinutes(5);

    public static TimerBackendRuntimeOptions FromConfiguration(IConfiguration configuration)
        => new()
        {
            RequestTimeout = ReadSeconds(configuration, "RequestTimeoutSeconds", 15, 300),
            WorkerShutdownTimeout = ReadSeconds(configuration, "WorkerShutdownTimeoutSeconds", 15, 120),
            WorkerMaxLag = ReadSeconds(configuration, "WorkerMaxLagSeconds", 300, 86400),
        };

    private static TimeSpan ReadSeconds(IConfiguration configuration, string name, int fallback, int maximum)
    {
        var path = $"TimerBackend:Runtime:{name}";
        var raw = configuration[path];
        if (raw is null) return TimeSpan.FromSeconds(fallback);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value < 1 || value > maximum)
            throw new InvalidOperationException($"{path} must be an integer from 1 through {maximum}.");
        return TimeSpan.FromSeconds(value);
    }
}
