/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Sharp.Shared.Types;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Managers.Request;

internal static class BackendRpcMapper
{
    public static MapProfile ToProfile(MapInfoDto source)
    {
        var tier = new byte[Math.Max(MapProfile.DefaultTrackCount, source.Tier.Length)];
        source.Tier.CopyTo(tier, 0);

        return new MapProfile
        {
            MapId         = source.MapId,
            MapName       = source.MapName,
            WorkshopId    = source.WorkshopId,
            Stages        = source.Stages,
            Bonuses       = source.Bonuses,
            Tier          = tier,
            TotalPlayTime = source.TotalPlayTime,
            PlayCount     = source.PlayCount,
            AddedAt       = source.AddedAt,
            LastPlayedAt  = source.LastPlayedAt,
            Ranked        = source.Ranked,
        };
    }

    public static IReadOnlyList<MapProfile> ToProfiles(MapInfoDto[] source)
    {
        var result = new MapProfile[source.Length];

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = ToProfile(source[i]);
        }

        return result;
    }

    public static IReadOnlyList<RunRecord> ToRecords(RecordDto[] source)
    {
        var result = new RunRecord[source.Length];

        for (var i = 0; i < result.Length; i++)
        {
            var record = source[i];

            result[i] = new RunRecord
            {
                Id             = record.Id,
                RunDate        = DateTimeOffset.FromUnixTimeMilliseconds(record.RunDateUnixTimeMilliseconds).UtcDateTime,
                SteamId        = record.SteamId,
                PlayerName     = record.PlayerName,
                MapId          = record.MapId,
                Style          = record.Style,
                Track          = record.Track,
                Stage          = record.Stage,
                Time           = record.Time,
                Jumps          = record.Jumps,
                Strafes        = record.Strafes,
                Sync           = record.Sync,
                VelocityStartX = record.StartX,
                VelocityStartY = record.StartY,
                VelocityStartZ = record.StartZ,
                VelocityAvgX   = record.AverageX,
                VelocityAvgY   = record.AverageY,
                VelocityAvgZ   = record.AverageZ,
                VelocityEndX   = record.EndX,
                VelocityEndY   = record.EndY,
                VelocityEndZ   = record.EndZ,
            };
        }

        return result;
    }

    public static IReadOnlyList<RunCheckpoint> ToCheckpoints(RecordCheckpointDto[] source)
    {
        var result = new RunCheckpoint[source.Length];

        for (var i = 0; i < result.Length; i++)
        {
            var checkpoint = source[i];
            var motion     = checkpoint.Motion;

            result[i] = new RunCheckpoint
            {
                Id              = checkpoint.Id,
                RecordId        = checkpoint.RecordId,
                CheckpointIndex = checkpoint.Index,
                Time            = checkpoint.Time,
                Sync            = checkpoint.Sync,
                VelocityStartX  = motion.StartX,
                VelocityStartY  = motion.StartY,
                VelocityStartZ  = motion.StartZ,
                VelocityAvgX    = motion.AverageX,
                VelocityAvgY    = motion.AverageY,
                VelocityAvgZ    = motion.AverageZ,
                VelocityMaxX    = motion.MaxX,
                VelocityMaxY    = motion.MaxY,
                VelocityMaxZ    = motion.MaxZ,
                VelocityEndX    = motion.EndX,
                VelocityEndY    = motion.EndY,
                VelocityEndZ    = motion.EndZ,
            };
        }

        return result;
    }

    public static IReadOnlyList<ZoneData> ToZones(ZoneDto[] source)
    {
        var result = new ZoneData[source.Length];

        for (var i = 0; i < result.Length; i++)
        {
            var zone = source[i];

            result[i] = new ZoneData
            {
                Id             = zone.Id,
                Type           = (EZoneType)zone.Type,
                Track          = zone.Track,
                Sequence       = zone.Sequence,
                Mins           = ToVector(zone.Mins),
                Maxs           = ToVector(zone.Maxs),
                Center         = ToVector(zone.Center),
                TeleportOrigin = zone.TeleportOrigin is { } origin ? ToVector(origin) : null,
                TeleportAngles = zone.TeleportAngles is { } angles ? ToVector(angles) : null,
                Config         = zone.Config,
            };
        }

        return result;
    }

    public static ZoneDto[] ToDtos(IReadOnlyList<ZoneData> source)
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
                Mins           = ToDto(zone.Mins),
                Maxs           = ToDto(zone.Maxs),
                Center         = ToDto(zone.Center),
                TeleportOrigin = zone.TeleportOrigin is { } origin ? ToDto(origin) : null,
                TeleportAngles = zone.TeleportAngles is { } angles ? ToDto(angles) : null,
                Config         = zone.Config,
            };
        }

        return result;
    }

    public static PlayerSummary ToSummary(PlayerSummaryDto source)
    {
        var styles = new PlayerStyleSummary[source.Styles.Length];

        for (var i = 0; i < styles.Length; i++)
        {
            var style = source.Styles[i];

            styles[i] = new PlayerStyleSummary(style.Style,
                                               style.MapsCompleted,
                                               style.BonusesCompleted,
                                               style.MapRecords,
                                               style.BonusRecords,
                                               style.StageRecords);
        }

        return new PlayerSummary
        {
            Name         = source.Name,
            Points       = source.Points,
            JoinedAt     = source.JoinedAt,
            TotalMaps    = source.TotalMaps,
            TotalBonuses = source.TotalBonuses,
            PlayTime     = source.PlayTime,
            Styles       = styles,
        };
    }

    public static ScoreQueueResult ToResult(ScoreJobsDto source)
        => new (source.MapFound, source.MapsAffected, source.BoardsQueued, source.FailedMaps);

    public static DeletedRun ToDeletedRun(DeletedRunDto source)
        => new (source.RunId, source.SteamId, source.Kind == RunKind.Stage, source.Style, source.Track, source.Stage,
                source.WasBest, source.ReplayUrls ?? []);

    public static WipedPlayer ToWipedPlayer(WipedPlayerDto source)
        => new (source.Maps, source.Runs,
                (source.Deleted ?? []).Select(x => new WipedRun(x.MapName, x.Time, ToDeletedRun(x.Run))).ToArray());

    private static Vector ToVector(VectorDto source)
        => new (source.X, source.Y, source.Z);

    private static VectorDto ToDto(Vector source)
        => new () { X = source.X, Y = source.Y, Z = source.Z };
}
