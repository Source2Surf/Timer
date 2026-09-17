using System;
using System.Globalization;
using System.Threading.Tasks;
using Timer.Backend.Configuration;
using Timer.RequestManager.Backend;

namespace Timer.Backend.Administration;

/// <summary>
/// Parses one-shot, process-local administrative commands before ASP.NET receives the remaining
/// arguments as configuration switches. No command declared here registers an HTTP or gRPC route.
/// </summary>
internal static class BackendAdministrativeCli
{
    internal static BackendAdministrativeInvocation Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return new BackendAdministrativeInvocation(BackendAdministrativeOperation.Serve, args);
        }

        if (string.Equals(args[0], "keygen", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("keygen is no longer supported; the write API does not use API keys.");
        }

        if (string.Equals(args[0], "migrate", StringComparison.OrdinalIgnoreCase))
        {
            return new BackendAdministrativeInvocation(BackendAdministrativeOperation.Migrate, args[1..]);
        }

        if (string.Equals(args[0], "convert-run-dates", StringComparison.OrdinalIgnoreCase))
        {
            var backupConfirmed = false;
            var configurationArguments = new string[args.Length - 1];
            var configurationArgumentCount = 0;
            for (var index = 1; index < args.Length; index++)
            {
                if (string.Equals(args[index], "--backup-confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    backupConfirmed = true;
                    continue;
                }

                configurationArguments[configurationArgumentCount++] = args[index];
            }

            if (!backupConfirmed)
            {
                throw new ArgumentException(
                    "convert-run-dates requires --backup-confirmed after all writers are stopped and a restorable backup has been verified.");
            }

            if (configurationArgumentCount != configurationArguments.Length)
            {
                Array.Resize(ref configurationArguments, configurationArgumentCount);
            }

            return new BackendAdministrativeInvocation(BackendAdministrativeOperation.ConvertRunDates,
                                                       configurationArguments,
                                                       BackupConfirmed: true);
        }

        if (string.Equals(args[0], "set-tier", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                throw new ArgumentException("Usage: set-tier <map> <tier> [configuration switches]");
            }

            if (string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("set-tier requires a map name.");
            }

            if (!byte.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tier)
                || tier == 0)
            {
                throw new ArgumentException("set-tier tier must be an integer from 1 through 255.");
            }

            return new BackendAdministrativeInvocation(
                BackendAdministrativeOperation.SetTier,
                args[3..],
                args[1],
                tier);
        }

        if (string.Equals(args[0], "recalc-scores", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("Usage: recalc-scores <map|all> [configuration switches]");
            }

            return new BackendAdministrativeInvocation(
                BackendAdministrativeOperation.RecalculateScores,
                args[2..],
                args[1]);
        }

        return new BackendAdministrativeInvocation(BackendAdministrativeOperation.Serve, args);
    }

    internal static async Task<TimerBackendScoreAdministrationResult> ExecuteAsync(
        BackendAdministrativeInvocation invocation,
        TimerBackendStorage storage,
        TimerWriteApiOptions writeApiOptions)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(writeApiOptions);

        var administration = new TimerBackendScoreAdministration(storage);
        return invocation.Operation switch
        {
            BackendAdministrativeOperation.SetTier => await administration.SetMapTierAsync(
                invocation.MapName!, invocation.Tier, writeApiOptions.StyleFactors),
            BackendAdministrativeOperation.RecalculateScores
                when string.Equals(invocation.MapName, "all", StringComparison.OrdinalIgnoreCase)
                => await administration.RequeueAllScoresAsync(writeApiOptions.StyleFactors),
            BackendAdministrativeOperation.RecalculateScores => await administration.RequeueScoresAsync(
                invocation.MapName!, writeApiOptions.StyleFactors),
            _ => throw new InvalidOperationException($"{invocation.Operation} is not an administrative score command."),
        };
    }

    internal static string FormatCompletion(
        BackendAdministrativeInvocation invocation,
        TimerBackendScoreAdministrationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var target = invocation.Operation == BackendAdministrativeOperation.RecalculateScores
                     && string.Equals(invocation.MapName, "all", StringComparison.OrdinalIgnoreCase)
                         ? "all maps"
                         : $"map '{invocation.MapName}'";
        var tier = invocation.Operation == BackendAdministrativeOperation.SetTier
                     ? $" Tier {result.PreviousTier} -> {result.CurrentTier}."
                     : string.Empty;
        return $"Completed score administration for {target}.{tier} "
             + $"Queued {result.BoardsQueued} board(s) across {result.MapsAffected} map(s); "
             + $"reactivated {result.DeadLettersRequeued} dead-lettered board(s); "
             + $"skipped {result.DisabledStyleBoardsSkipped} board(s) whose styles are absent from the current policy.";
    }
}

internal enum BackendAdministrativeOperation
{
    Serve,
    Migrate,
    ConvertRunDates,
    SetTier,
    RecalculateScores,
}

internal sealed record BackendAdministrativeInvocation(
    BackendAdministrativeOperation Operation,
    string[] ConfigurationArguments,
    string? MapName = null,
    byte Tier = 0,
    bool BackupConfirmed = false);
