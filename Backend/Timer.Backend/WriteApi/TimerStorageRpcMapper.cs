using System;
using System.Collections.Generic;
using Sharp.Shared.Types;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;
using Timer.Backend.Storage;

namespace Timer.Backend.WriteApi;

internal static class TimerStorageRpcMapper
{
    public static MapInfoDto ToDto(MapProfile source)
        => new ()
        {
            MapId         = source.MapId,
            MapName       = source.MapName,
            WorkshopId    = source.WorkshopId,
            Stages        = source.Stages,
            Bonuses       = source.Bonuses,
            Tier          = source.Tier,
            TotalPlayTime = source.TotalPlayTime,
            PlayCount     = source.PlayCount,
            AddedAt       = source.AddedAt,
            LastPlayedAt  = source.LastPlayedAt,
            Ranked        = source.Ranked,
        };

    public static MapInfoDto[] ToDto(IReadOnlyList<MapProfile> source)
    {
        var result = new MapInfoDto[source.Count];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ToDto(source[i]);
        }

        return result;
    }

    public static RecordDto ToDto(RunRecord source)
        => new ()
        {
            Id                          = source.Id,
            RunDateUnixTimeMilliseconds = ToUnixTimeMilliseconds(source.RunDate),
            SteamId                     = source.SteamId,
            PlayerName                  = source.PlayerName,
            MapId                       = source.MapId,
            Style                       = source.Style,
            Track                       = source.Track,
            Stage                       = source.Stage,
            Time                        = source.Time,
            Jumps                       = source.Jumps,
            Strafes                     = source.Strafes,
            Sync                        = source.Sync,
            StartX                      = source.VelocityStartX,
            StartY                      = source.VelocityStartY,
            StartZ                      = source.VelocityStartZ,
            AverageX                    = source.VelocityAvgX,
            AverageY                    = source.VelocityAvgY,
            AverageZ                    = source.VelocityAvgZ,
            EndX                        = source.VelocityEndX,
            EndY                        = source.VelocityEndY,
            EndZ                        = source.VelocityEndZ,
        };

    public static RecordDto[] ToDto(IReadOnlyList<RunRecord> source)
    {
        var result = new RecordDto[source.Count];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ToDto(source[i]);
        }

        return result;
    }

    public static DeletedRunDto ToDto(TimerBackendDeletedRun source)
        => new()
        {
            RunId      = source.RunId,
            SteamId    = source.SteamId,
            Kind       = source.StageRun ? RunKind.Stage : RunKind.Main,
            Style      = source.Style,
            Track      = source.Track,
            Stage      = source.Stage,
            WasBest    = source.WasBest,
            ReplayUrls = [.. source.ReplayUrls],
        };

    public static WipedPlayerDto ToDto(TimerBackendWipedPlayer source)
    {
        var deleted = new WipedRunDto[source.Deleted.Count];

        for (var i = 0; i < deleted.Length; i++)
        {
            var run = source.Deleted[i];
            deleted[i] = new WipedRunDto { MapName = run.MapName, Time = run.Time, Run = ToDto(run.Run) };
        }

        return new WipedPlayerDto { Maps = source.Maps, Runs = source.Runs, Deleted = deleted };
    }

    public static RecordCheckpointDto[] ToDto(IReadOnlyList<RunCheckpoint> source)
    {
        var result = new RecordCheckpointDto[source.Count];

        for (var i = 0; i < result.Length; i++)
        {
            var checkpoint = source[i];

            result[i] = new RecordCheckpointDto
            {
                Id       = checkpoint.Id,
                RecordId = checkpoint.RecordId,
                Index    = checkpoint.CheckpointIndex,
                Time     = checkpoint.Time,
                Sync     = checkpoint.Sync,
                Motion = new MotionDto
                {
                    StartX   = checkpoint.VelocityStartX,
                    StartY   = checkpoint.VelocityStartY,
                    StartZ   = checkpoint.VelocityStartZ,
                    AverageX = checkpoint.VelocityAvgX,
                    AverageY = checkpoint.VelocityAvgY,
                    AverageZ = checkpoint.VelocityAvgZ,
                    MaxX     = checkpoint.VelocityMaxX,
                    MaxY     = checkpoint.VelocityMaxY,
                    MaxZ     = checkpoint.VelocityMaxZ,
                    EndX     = checkpoint.VelocityEndX,
                    EndY     = checkpoint.VelocityEndY,
                    EndZ     = checkpoint.VelocityEndZ,
                },
            };
        }

        return result;
    }

    public static ZoneDto[] ToDto(IReadOnlyList<ZoneData> source)
    {
        var result = new ZoneDto[source.Count];

        for (var i = 0; i < result.Length; i++)
        {
            var zone = source[i];

            result[i] = new ZoneDto
            {
                Id             = zone.Id,
                Type           = (int)zone.Type,
                Track          = zone.Track,
                Sequence       = zone.Sequence,
                Mins           = Vector(zone.Mins),
                Maxs           = Vector(zone.Maxs),
                Center         = Vector(zone.Center),
                TeleportOrigin = zone.TeleportOrigin is { } origin ? Vector(origin) : null,
                TeleportAngles = zone.TeleportAngles is { } angles ? Vector(angles) : null,
                Config         = zone.Config,
            };
        }

        return result;
    }

    public static List<ZoneData> ToZones(ZoneDto[] source)
    {
        var result = new List<ZoneData>(source.Length);

        foreach (var zone in source)
        {
            if (zone is null || zone.Mins is null || zone.Maxs is null || zone.Center is null
                || !Enum.IsDefined((EZoneType)zone.Type))
            {
                throw TimerWriteRpcErrors.InvalidArgument();
            }

            result.Add(new ZoneData
            {
                Type           = (EZoneType)zone.Type,
                Track          = zone.Track,
                Sequence       = zone.Sequence,
                Mins           = Vector(zone.Mins),
                Maxs           = Vector(zone.Maxs),
                Center         = Vector(zone.Center),
                TeleportOrigin = zone.TeleportOrigin is { } origin ? Vector(origin) : null,
                TeleportAngles = zone.TeleportAngles is { } angles ? Vector(angles) : null,
                Config         = zone.Config,
            });
        }

        return result;
    }

    public static PlayerSummaryDto ToDto(PlayerSummary source)
    {
        var styles = new StyleSummaryDto[source.Styles.Count];

        for (var i = 0; i < styles.Length; i++)
        {
            var style = source.Styles[i];

            styles[i] = new StyleSummaryDto
            {
                Style            = style.Style,
                MapsCompleted    = style.MapsCompleted,
                BonusesCompleted = style.BonusesCompleted,
                MapRecords       = style.MapRecords,
                BonusRecords     = style.BonusRecords,
                StageRecords     = style.StageRecords,
            };
        }

        return new PlayerSummaryDto
        {
            TotalMaps    = source.TotalMaps,
            TotalBonuses = source.TotalBonuses,
            PlayTime     = source.PlayTime,
            Styles       = styles,
            Name         = source.Name,
            Points       = source.Points,
            JoinedAt     = source.JoinedAt,
        };
    }

    public static ScoreJobsDto ToDto(TimerBackendScoreAdministrationResult source)
    {
        var failedMaps = new string[source.FailedMaps.Count];

        for (var i = 0; i < failedMaps.Length; i++)
        {
            failedMaps[i] = source.FailedMaps[i];
        }

        return new ScoreJobsDto
        {
            MapFound                   = source.MapFound,
            MapsAffected               = source.MapsAffected,
            BoardsQueued               = source.BoardsQueued,
            DeadLettersRequeued        = source.DeadLettersRequeued,
            DisabledStyleBoardsSkipped = source.DisabledStyleBoardsSkipped,
            PreviousTier               = source.PreviousTier,
            CurrentTier                = source.CurrentTier,
            FailedMaps                 = failedMaps,
        };
    }

    private static VectorDto Vector(Vector source)
        => new () { X = source.X, Y = source.Y, Z = source.Z };

    private static Vector Vector(VectorDto source)
        => new (source.X, source.Y, source.Z);

    // Storage reads UTC; a provider may hand it back as Unspecified.
    private static long ToUnixTimeMilliseconds(DateTime value)
        => new DateTimeOffset(value.Kind == DateTimeKind.Local
                                  ? value.ToUniversalTime()
                                  : DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
