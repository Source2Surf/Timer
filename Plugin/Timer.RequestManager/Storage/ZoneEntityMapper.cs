using System;
using Sharp.Shared.Types;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Timer.RequestManager.Storage;

internal static class ZoneEntityMapper
{
    public static ZoneEntity ToEntity(ZoneData data, ulong mapId)
    {
        return new ZoneEntity
        {
            MapId          = mapId,
            Type           = data.Type,
            Track          = (ushort)data.Track,
            Sequence       = (ushort)data.Sequence,
            Mins           = data.Mins,
            Maxs           = data.Maxs,
            Center         = data.Center,
            Angles         = new Vector(),
            TeleportOrigin = data.TeleportOrigin,
            TeleportAngles = data.TeleportAngles,
            Config         = data.Config,
        };
    }

    public static ZoneData ToData(ZoneEntity entity)
    {
        return new ZoneData
        {
            Id             = entity.Id,
            Type           = entity.Type,
            Track          = entity.Track,
            Sequence       = entity.Sequence,
            Mins           = entity.Mins,
            Maxs           = entity.Maxs,
            Center         = entity.Center,
            TeleportOrigin = entity.TeleportOrigin,
            TeleportAngles = entity.TeleportAngles,
            Config         = entity.Config,
        };
    }
}
