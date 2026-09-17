using System;
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

        var tiers = new byte[source.Tier.Length];
        Array.Copy(source.Tier, tiers, tiers.Length);

        return new MapProfileDto
        {
            MapId               = ToUnsignedId(source.MapId),
            MapName             = source.MapName,
            Stages              = source.Stages,
            Bonuses             = source.Bonuses,
            Tier                = tiers,
            TotalPlayTimeMicros = ToMicroseconds(source.TotalPlayTime),
            PlayCount           = source.PlayCount,
        };
    }

    public static RunRecordDto ToDto(RunRecord source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new RunRecordDto
        {
            Id             = ToSignedId(source.Id),
            RunDate        = ToUtc(source.RunDate).ToUnixTimeMilliseconds(),
            SteamId        = ToUnsignedId(source.SteamId),
            PlayerName     = string.IsNullOrEmpty(source.PlayerName) ? null : source.PlayerName,
            MapId          = ToUnsignedId(source.MapId),
            Style          = source.Style,
            Track          = source.Track,
            Stage          = source.Stage,
            TimeMicros     = ToMicroseconds(source.Time),
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
    }

    public static RunCheckpointDto ToDto(RunCheckpoint source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new RunCheckpointDto
        {
            Id              = ToSignedId(source.Id),
            RecordId        = ToSignedId(source.RecordId),
            CheckpointIndex = source.CheckpointIndex,
            TimeMicros      = ToMicroseconds(source.Time),
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
    }

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
