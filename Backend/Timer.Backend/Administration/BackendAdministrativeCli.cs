using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Timer.Backend.Configuration;
using Timer.Backend.Storage;

namespace Timer.Backend.Administration;

/// <summary>
/// Parses one-shot, process-local administrative commands before ASP.NET receives the remaining
/// arguments as configuration switches. No command declared here registers an HTTP or gRPC route.
/// </summary>
internal static class BackendAdministrativeCli
{
    private static readonly string[] CommandNames = ["migrate", "convert-run-dates", "set-tier", "recalc-scores", "keygen"];

    internal static BackendAdministrativeInvocation Parse(string[] args)
    {
        var invocation = ParseCommand(args);
        EnsureOnlyConfigurationSwitches(invocation.ConfigurationArguments);
        return invocation;
    }

    /// <summary>
    /// Everything left after a command (or every argument when serving) is handed to the ASP.NET
    /// command-line configuration provider, which silently drops bare words. A typo such as
    /// "recalc-score all", a command placed after a switch, or an extra positional argument would
    /// otherwise start the full server (with the write API and worker) instead of failing.
    /// </summary>
    private static void EnsureOnlyConfigurationSwitches(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.StartsWith('-') || argument.StartsWith('/'))
            {
                if (!argument.Contains('='))
                {
                    index++; // "--Key Value": the next argument is this switch's value.
                }

                continue;
            }

            if (argument.Contains('='))
            {
                continue; // "Key=Value" is also accepted by the configuration provider.
            }

            var hint = CommandNames.Contains(argument, StringComparer.OrdinalIgnoreCase)
                ? " Administrative commands must be the first argument."
                : string.Empty;
            throw new ArgumentException(
                $"Unrecognized argument '{argument}'.{hint} Configuration switches use --Key=Value or --Key Value.");
        }
    }

    private static BackendAdministrativeInvocation ParseCommand(string[] args)
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
        EnsureExplicitScorePolicy(writeApiOptions);

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

    /// <summary>
    /// Score commands persist these factors onto every board they queue. The implicit style-0
    /// default a write instance falls back to would silently replace the serving policy when
    /// the command runs without that instance's configuration, so require it explicitly.
    /// </summary>
    internal static void EnsureExplicitScorePolicy(TimerWriteApiOptions writeApiOptions)
    {
        ArgumentNullException.ThrowIfNull(writeApiOptions);

        if (!writeApiOptions.HasExplicitStyleFactors || !writeApiOptions.StyleFactors.ContainsKey(0))
        {
            throw new InvalidOperationException(
                $"Score administration requires {TimerWriteApiOptions.SectionName}:StyleFactors to be configured "
              + "explicitly, including style 0, with the same factors as the serving write instance.");
        }
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
        var completion = $"Completed score administration for {target}.{tier} "
                       + $"Queued {result.BoardsQueued} board(s) across {result.MapsAffected} map(s); "
                       + $"reactivated {result.DeadLettersRequeued} dead-lettered board(s); "
                       + $"skipped {result.DisabledStyleBoardsSkipped} board(s) whose styles are absent from the current policy.";
        return result.FailedMaps.Count == 0
            ? completion
            : completion
            + Environment.NewLine
            + $"{result.FailedMaps.Count} map(s) were NOT requeued:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, result.FailedMaps.Select(failure => "  " + failure));
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
