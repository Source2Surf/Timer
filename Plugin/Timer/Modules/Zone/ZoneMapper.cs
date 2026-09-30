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
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Zone;

internal static class ZoneMapper
{
    /// <summary>
    /// Converts a runtime ZoneInfo to a ZoneData transfer model for persistence.
    /// Computes Mins/Maxs/Center from Corner1/Corner2.
    /// </summary>
    public static ZoneData ToZoneData(ZoneInfo info)
    {
        var mins = new Vector(
            Math.Min(info.Corner1.X, info.Corner2.X),
            Math.Min(info.Corner1.Y, info.Corner2.Y),
            Math.Min(info.Corner1.Z, info.Corner2.Z));

        var maxs = new Vector(
            Math.Max(info.Corner1.X, info.Corner2.X),
            Math.Max(info.Corner1.Y, info.Corner2.Y),
            Math.Max(info.Corner1.Z, info.Corner2.Z));

        var center = (info.Corner1 + info.Corner2) / 2.0f;

        return new ZoneData
        {
            Type           = info.ZoneType,
            Track          = info.Track,
            Sequence       = info.Data,
            Mins           = mins,
            Maxs           = maxs,
            Center         = center,
            TeleportOrigin = info.TeleportOrigin,
            TeleportAngles = info.TeleportAngles,
        };
    }

    /// <summary>
    /// Converts a ZoneData transfer model back to a runtime ZoneInfo.
    /// Maps Mins→Corner1, Maxs→Corner2, Center→Origin, sets Prebuilt=false.
    /// </summary>
    public static ZoneInfo ToZoneInfo(ZoneData data)
    {
        return new ZoneInfo
        {
            ZoneType       = data.Type,
            Track          = data.Track,
            Data           = data.Sequence,
            Corner1        = data.Mins,
            Corner2        = data.Maxs,
            Origin         = data.Center,
            TeleportOrigin = data.TeleportOrigin,
            TeleportAngles = data.TeleportAngles,
            Prebuilt       = false,
        };
    }
}
