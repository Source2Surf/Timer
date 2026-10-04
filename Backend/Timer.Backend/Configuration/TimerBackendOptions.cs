using System;
using Microsoft.Extensions.Configuration;

namespace Timer.Backend.Configuration;

/// <summary>
/// Explicit deployment configuration for the standalone API. The database credentials live only
/// in this process; game servers reach the data through its gRPC services.
/// </summary>
internal sealed record TimerBackendOptions(string DatabaseType,
                                           string ConnectionString,
                                           bool   InitializeSchema,
                                           bool   AllowReadRepair,
                                           bool   EnableOutboxWorker)
{
    public const string SectionName = "TimerBackend";

    /// <summary>
    /// A write-serving backend must also process its durable score-recalculation work.
    /// Multiple write replicas may run workers safely because the SQL outbox uses leases.
    /// A read-only role can still opt into a worker explicitly.
    /// </summary>
    public bool ShouldRunOutboxWorker(bool writeApiEnabled)
        => EnableOutboxWorker || writeApiEnabled;

    public static TimerBackendOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var databaseType = configuration[$"{SectionName}:Database:Type"];
        // The shipped appsettings.json declares an empty primary value; treat blank as unset
        // so ConnectionStrings:TimerBackend (or its environment variable) still applies.
        var connectionString = configuration[$"{SectionName}:Database:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            connectionString = configuration.GetConnectionString("TimerBackend");
        }
        var initializeSchemaRaw = configuration[$"{SectionName}:InitializeSchema"];
        var allowReadRepairRaw = configuration[$"{SectionName}:AllowReadRepair"];
        var enableOutboxWorkerRaw = configuration[$"{SectionName}:EnableOutboxWorker"];

        if (string.IsNullOrWhiteSpace(databaseType))
        {
            throw new InvalidOperationException(
                $"Missing {SectionName}:Database:Type. Use mysql or postgresql.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing {SectionName}:Database:ConnectionString or ConnectionStrings:TimerBackend.");
        }

        if (!string.IsNullOrWhiteSpace(initializeSchemaRaw)
            && !bool.TryParse(initializeSchemaRaw, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:InitializeSchema must be true or false.");
        }

        if (!string.IsNullOrWhiteSpace(allowReadRepairRaw)
            && !bool.TryParse(allowReadRepairRaw, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:AllowReadRepair must be true or false.");
        }

        if (!string.IsNullOrWhiteSpace(enableOutboxWorkerRaw)
            && !bool.TryParse(enableOutboxWorkerRaw, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:EnableOutboxWorker must be true or false.");
        }

        return new TimerBackendOptions(databaseType,
                                       connectionString,
                                       bool.TryParse(initializeSchemaRaw, out var initializeSchema)
                                           && initializeSchema,
                                       bool.TryParse(allowReadRepairRaw, out var allowReadRepair)
                                           && allowReadRepair,
                                       bool.TryParse(enableOutboxWorkerRaw, out var enableOutboxWorker)
                                           && enableOutboxWorker);
    }
}
