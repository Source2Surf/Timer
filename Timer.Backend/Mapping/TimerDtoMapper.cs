using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Source2Surf.Timer.Backend.Contracts;
using Source2Surf.Timer.Shared.Models;

namespace Timer.Backend.Mapping;

/// <summary>
/// Converts game/storage models into the deliberately primitive HTTP v1 contract.
/// All integer identifiers are formatted invariantly, and all duration conversion follows
/// one explicit seconds-to-microseconds rounding rule at the transport boundary.
/// </summary>
internal static class TimerDtoMapper
{
    private const double MicrosecondsPerSecond = 1_000_000d;

    public static MapProfileDto ToDto(MapProfile source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tiers = new int[source.Tier.Length];
        for (var index = 0; index < tiers.Length; index++)
        {
            tiers[index] = source.Tier[index];
        }

        return new MapProfileDto
        {
            MapId               = ToUnsignedId(source.MapId),
            MapName             = source.MapName,
            Stages              = source.Stages,
            Bonuses             = source.Bonuses,
            Tier                = tiers,
            TotalPlayTimeMicros = ToMicrosecondsOrZero(source.TotalPlayTime),
            PlayCount           = source.PlayCount,
        };
    }

    public static RunRecordDto ToDto(RunRecord source)
        => TryToDto(source, out var dto)
            ? dto
            : throw new InvalidOperationException(
                "Stored duration must be finite and non-negative before it can be represented by the HTTP contract.");

    /// <summary>
    /// Maps a record unless its stored time cannot be represented (negative, NaN, or too large),
    /// so a list endpoint can skip one corrupt legacy row instead of failing the whole response.
    /// </summary>
    public static bool TryToDto(RunRecord source, [NotNullWhen(true)] out RunRecordDto? dto)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!TryToMicroseconds(source.Time, out var timeMicros))
        {
            dto = null;
            return false;
        }

        dto = new RunRecordDto
        {
            Id             = ToSignedId(source.Id),
            RunDate        = ToUtc(source.RunDate).ToUnixTimeMilliseconds(),
            SteamId        = ToUnsignedId(source.SteamId),
            PlayerName     = string.IsNullOrEmpty(source.PlayerName) ? null : source.PlayerName,
            MapId          = ToUnsignedId(source.MapId),
            Style          = source.Style,
            Track          = source.Track,
            Stage          = source.Stage,
            TimeMicros     = timeMicros,
            Jumps          = source.Jumps,
            Strafes        = source.Strafes,
            Sync           = source.Sync,
            VelocityStartX = source.VelocityStartX,
            VelocityStartY = source.VelocityStartY,
            VelocityStartZ = source.VelocityStartZ,
            VelocityAvgX   = source.VelocityAvgX,
            VelocityAvgY   = source.VelocityAvgY,
            VelocityAvgZ   = source.VelocityAvgZ,
            VelocityEndX   = source.VelocityEndX,
            VelocityEndY   = source.VelocityEndY,
            VelocityEndZ   = source.VelocityEndZ,
        };
        return true;
    }

    public static RunCheckpointDto ToDto(RunCheckpoint source)
        => TryToDto(source, out var dto)
            ? dto
            : throw new InvalidOperationException(
                "Stored duration must be finite and non-negative before it can be represented by the HTTP contract.");

    public static bool TryToDto(RunCheckpoint source, [NotNullWhen(true)] out RunCheckpointDto? dto)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!TryToMicroseconds(source.Time, out var timeMicros))
        {
            dto = null;
            return false;
        }

        dto = new RunCheckpointDto
        {
            Id              = ToSignedId(source.Id),
            RecordId        = ToSignedId(source.RecordId),
            CheckpointIndex = source.CheckpointIndex,
            TimeMicros      = timeMicros,
            Sync            = source.Sync,
            VelocityStartX  = source.VelocityStartX,
            VelocityStartY  = source.VelocityStartY,
            VelocityStartZ  = source.VelocityStartZ,
            VelocityAvgX    = source.VelocityAvgX,
            VelocityAvgY    = source.VelocityAvgY,
            VelocityAvgZ    = source.VelocityAvgZ,
            VelocityMaxX    = source.VelocityMaxX,
            VelocityMaxY    = source.VelocityMaxY,
            VelocityMaxZ    = source.VelocityMaxZ,
            VelocityEndX    = source.VelocityEndX,
            VelocityEndY    = source.VelocityEndY,
            VelocityEndZ    = source.VelocityEndZ,
        };
        return true;
    }

    public static bool TryToMicroseconds(float seconds, out long microseconds)
    {
        microseconds = 0;
        if (!float.IsFinite(seconds) || seconds < 0f)
        {
            return false;
        }

        var rounded = Math.Round(seconds * MicrosecondsPerSecond, MidpointRounding.AwayFromZero);
        if (rounded >= long.MaxValue)
        {
            return false;
        }

        microseconds = (long)rounded;
        return true;
    }

    /// <summary>
    /// For aggregate counters (total play time): an unrepresentable stored value reports 0
    /// rather than failing the whole profile/stats response.
    /// </summary>
    public static long ToMicrosecondsOrZero(float seconds)
        => TryToMicroseconds(seconds, out var microseconds) ? microseconds : 0;

    public static long ToMicroseconds(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0f)
        {
            throw new InvalidOperationException(
                "Stored duration must be finite and non-negative before it can be represented by the HTTP contract.");
        }

        return checked((long)Math.Round(seconds * MicrosecondsPerSecond, MidpointRounding.AwayFromZero));
    }

    public static string ToUnsignedId(ulong value)
        => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Storage exposes historical identity values through signed longs. Preserve their raw
    /// 64-bit identity representation by formatting as an unsigned decimal value.
    /// </summary>
    public static string ToSignedId(long value)
        => unchecked((ulong)value).ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset ToUtc(DateTime value)
    {
        // Timer storage writes UTC. Some database providers materialize datetime values as
        // Unspecified, so preserve their intended UTC clock value instead of treating it as
        // the API host's local time.
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        return new DateTimeOffset(utc);
    }
}
