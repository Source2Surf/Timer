using System;
using Microsoft.Extensions.Configuration;

namespace Source2Surf.Timer.Configuration;

/// <summary>
/// Selects the score-write transport explicitly. Remote mode must never silently fall back
/// to local SQL after an ambiguous RPC result, since that would bypass SubmissionId fencing.
/// Other IRequestManager operations remain on the current provider during the score canary.
/// </summary>
internal sealed record ScoreWriteModeOptions(ScoreWriteMode Mode)
{
    internal const string SectionName = "Timer:ScoreWrite";

    public static ScoreWriteModeOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var raw = configuration[$"{SectionName}:Mode"];
        return new ScoreWriteModeOptions(raw?.Trim().ToLowerInvariant() switch
        {
            null or "" or "local-sql" => ScoreWriteMode.LocalSql,
            "remote-write" => ScoreWriteMode.RemoteWrite,
            _ => throw new InvalidOperationException(
                $"{SectionName}:Mode must be 'local-sql' or 'remote-write'."),
        });
    }
}

internal enum ScoreWriteMode
{
    LocalSql = 0,
    RemoteWrite = 1,
}
