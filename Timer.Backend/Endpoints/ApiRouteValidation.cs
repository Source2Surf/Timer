using System;
using System.Globalization;
using Source2Surf.Timer.Shared;

namespace Timer.Backend.Endpoints;

internal static class ApiRouteValidation
{
    public const int MaximumRecordLimit = 5000;

    public static bool TryNormalizeMapName(string? raw, out string mapName, out string error)
    {
        mapName = string.Empty;
        error   = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "mapName is required.";
            return false;
        }

        var normalized = raw.Trim();

        if (normalized.Length > 192)
        {
            error = "mapName must contain at most 192 characters.";
            return false;
        }

        foreach (var character in normalized)
        {
            if (char.IsControl(character) || character is '/' or '\\')
            {
                error = "mapName contains an unsupported character.";
                return false;
            }
        }

        mapName = normalized.ToLowerInvariant();
        return true;
    }

    public static bool TryParseSteamId(string? raw, out ulong steamId, out string error)
        => TryParseUnsignedId(raw, "steamId", out steamId, out error);

    public static bool TryParseRunId(string? raw, out ulong runId, out string error)
        => TryParseUnsignedId(raw, "runId", out runId, out error);

    public static bool TryParseLimit(string? raw, out int limit, out string error)
    {
        limit = MaximumRecordLimit;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit is < 1 or > MaximumRecordLimit)
        {
            error = $"limit must be an integer between 1 and {MaximumRecordLimit}.";
            return false;
        }

        return true;
    }

    public static bool TryParseOptionalStyle(string? raw, out int? style, out string error)
        => TryParseOptionalIndex(raw, "style", TimerConstants.MAX_STYLE, 0, out style, out error);

    public static bool TryParseOptionalTrack(string? raw, out int? track, out string error)
        => TryParseOptionalIndex(raw, "track", TimerConstants.MAX_TRACK, 0, out track, out error);

    public static bool TryParseOptionalStage(string? raw, out int? stage, out string error)
        => TryParseOptionalIndex(raw, "stage", TimerConstants.MAX_STAGE, 1, out stage, out error);

    private static bool TryParseUnsignedId(string? raw, string parameterName, out ulong value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw)
            || !ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            || value == 0)
        {
            error = $"{parameterName} must be a non-zero unsigned decimal string.";
            return false;
        }

        return true;
    }

    private static bool TryParseOptionalIndex(string? raw,
                                              string  parameterName,
                                              int     exclusiveMaximum,
                                              int     inclusiveMinimum,
                                              out int? value,
                                              out string error)
    {
        value = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            || parsed < inclusiveMinimum
            || parsed >= exclusiveMaximum)
        {
            error = $"{parameterName} must be an integer in [{inclusiveMinimum}, {exclusiveMaximum}).";
            return false;
        }

        value = parsed;
        return true;
    }
}
