using System;
using System.Collections.Generic;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    private static MapProfile ToMapProfile(MapEntity mapInfo, IReadOnlyList<MapTrackEntity> trackInfos)
    {
        var tiers = new byte[MapProfile.DefaultTrackCount];
        tiers[0] = (byte) mapInfo.Tier;

        foreach (var trackInfo in trackInfos)
        {
            if (trackInfo.Track >= tiers.Length)
            {
                continue;
            }

            tiers[trackInfo.Track] = (byte) trackInfo.Tier;
        }

        return new ()
        {
            MapId         = mapInfo.MapId,
            MapName       = mapInfo.File,
            WorkshopId    = mapInfo.WorkshopId,
            Stages        = mapInfo.Stages,
            Bonuses       = mapInfo.Bonuses,
            Tier          = tiers,
            PlayCount     = mapInfo.PlayCount,
            TotalPlayTime = mapInfo.TotalPlayTime,
            AddedAt       = mapInfo.AddedAtUnixMilliseconds,
            LastPlayedAt  = mapInfo.LastPlayedAtUnixMilliseconds,
            Ranked        = mapInfo.Unranked == 0,
        };
    }

    // Run times are whole 64 Hz server ticks. A time made from its ticks is exact where a MySQL FLOAT read back is
    // rounded to 6 digits, so it's taken when the ticks agree with the stored time. The time stays the authority:
    // a hand-edited one is shown as it is, and startup brings the ticks back in line with it.
    internal const float TicksPerSecond = 64f;

    internal static int TicksOf(float seconds)
        => checked((int)Math.Round(seconds * (double)TicksPerSecond));

    internal static float TimeOf(long ticks, float time)
    {
        if (ticks <= 0)
        {
            return time;
        }

        var exact = ticks / TicksPerSecond;

        return MathF.Abs(exact - time) <= (exact * 1e-5f) + 1e-4f ? exact : time;
    }

    private static RunRecord ToRunRecord(BoardRow row)
        => new ()
        {
            Id             = row.Id,
            RunDate        = FromUnixTimeMilliseconds(row.DateUnixTimeMilliseconds),
            SteamId        = unchecked((ulong)row.SteamId),
            PlayerName     = row.PlayerName ?? string.Empty,
            MapId          = unchecked((ulong)row.MapId),
            Style          = row.Style,
            Track          = row.Track,
            Stage          = row.Stage,
            Time           = TimeOf(row.Ticks, row.Time),
            Jumps          = (int)Math.Min(row.Jumps, int.MaxValue),
            Strafes        = (int)Math.Min(row.Strafes, int.MaxValue),
            Sync           = row.Sync,
            VelocityStartX = row.VelocityStartX,
            VelocityStartY = row.VelocityStartY,
            VelocityStartZ = row.VelocityStartZ,
            VelocityAvgX   = row.VelocityAvgX,
            VelocityAvgY   = row.VelocityAvgY,
            VelocityAvgZ   = row.VelocityAvgZ,
            VelocityEndX   = row.VelocityEndX,
            VelocityEndY   = row.VelocityEndY,
            VelocityEndZ   = row.VelocityEndZ,
        };

    // Only the columns a RunRecord needs, plus the player's name. Unsigned columns are read as signed:
    // SqlSugar converts unsigned values through reflection, about twice the cost of the whole row.
    private sealed class BoardRow
    {
        public long    Id                       { get; set; }
        public long    DateUnixTimeMilliseconds { get; set; }
        public long    SteamId                  { get; set; }
        public string? PlayerName               { get; set; }
        public long    MapId                    { get; set; }
        public int     Style                    { get; set; }
        public int     Track                    { get; set; }
        public int     Stage                    { get; set; }
        public float   Time                     { get; set; }
        public long    Ticks                    { get; set; }
        public long    Jumps                    { get; set; }
        public long    Strafes                  { get; set; }
        public float   Sync                     { get; set; }
        public float   VelocityStartX           { get; set; }
        public float   VelocityStartY           { get; set; }
        public float   VelocityStartZ           { get; set; }
        public float   VelocityAvgX             { get; set; }
        public float   VelocityAvgY             { get; set; }
        public float   VelocityAvgZ             { get; set; }
        public float   VelocityEndX             { get; set; }
        public float   VelocityEndY             { get; set; }
        public float   VelocityEndZ             { get; set; }
    }

    private static RunRecord ToRunRecord(RunEntity run)
        => new ()
        {
            Id             = (long) run.Id,
            RunDate        = FromUnixTimeMilliseconds(run.DateUnixTimeMilliseconds),
            SteamId        = unchecked((ulong)run.SteamId),
            MapId          = run.MapId,
            Style          = (int) run.Style,
            Track          = run.Track,
            Stage          = run.Stage,
            Time           = TimeOf(run.Ticks, run.Time),
            Jumps          = ToInt32(run.Jumps),
            Strafes        = ToInt32(run.Strafes),
            Sync           = run.Sync,
            VelocityStartX = run.VelocityStartX,
            VelocityStartY = run.VelocityStartY,
            VelocityStartZ = run.VelocityStartZ,
            VelocityAvgX   = run.VelocityAvgX,
            VelocityAvgY   = run.VelocityAvgY,
            VelocityAvgZ   = run.VelocityAvgZ,
            VelocityEndX   = run.VelocityEndX,
            VelocityEndY   = run.VelocityEndY,
            VelocityEndZ   = run.VelocityEndZ,
        };
}
