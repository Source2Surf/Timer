using System;
using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;

namespace Source2Surf.Timer.Configuration;

/// <summary>
/// Selects the score-write transport explicitly. Remote mode must never silently fall back
/// to local SQL after an ambiguous RPC result, since that would bypass SubmissionId fencing.
/// Other IRequestManager operations remain on the current provider during the score canary.
/// </summary>
internal sealed record ScoreWriteModeOptions(ScoreWriteMode Mode)
{
    // timer.jsonc's section, shared with the run submission sender's settings.
    internal const string SectionName = "score_write";

    internal static readonly FrozenSet<string> Keys = FrozenSet.ToFrozenSet(
    [
        "mode", "ruleset_version", "enabled", "endpoint", "rpc_deadline_milliseconds", "poll_interval_milliseconds",
        "shutdown_drain_timeout_milliseconds", "batch_size", "allow_insecure_loopback",
    ], StringComparer.OrdinalIgnoreCase);

    public static ScoreWriteModeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[$"{SectionName}:mode"];
        return new ScoreWriteModeOptions(raw?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local-sql" => ScoreWriteMode.LocalSql,
            "remote-write" => ScoreWriteMode.RemoteWrite,
            _ => throw new InvalidOperationException(
                $"{SectionName}:mode must be 'local-sql' or 'remote-write'."),
        });
    }
}

internal enum ScoreWriteMode
{
    LocalSql = 0,
    RemoteWrite = 1,
}
