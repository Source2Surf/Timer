﻿/*
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
 
using System.Text.Json.Serialization;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Zone;


internal class ZoneInfo : IZoneInfo
{
    public EZoneType   ZoneType       { get; init; } = EZoneType.Invalid;
    public int         Track          { get; set; }  = 0;

    [JsonIgnore]
    public EntityIndex Index { get; set; }

    public Vector Corner1    { get; init; } = new ();
    public Vector Corner2    { get; init; } = new ();

    [JsonIgnore]
    public string TargetName { get; init; } = string.Empty;

    public Vector?     TeleportOrigin { get; set; }
    public Vector?     TeleportAngles { get; set; }

    public Vector Origin { get; set; } = new ();

    public bool Prebuilt { get; init; }

    public int Data { get; set; } = 0; // Stage number for Stage zones, checkpoint index for Checkpoint zones

    [JsonIgnore]
    public IBaseEntity[]? Beams = null; // Only populated for Start and End zone types
}
