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
 
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Zone;
using Source2Surf.Timer.Types;

namespace Source2Surf.Timer.Modules.Zone;


internal class ZoneInfo : IZoneInfo
{
    public EZoneType   ZoneType       { get; init; } = EZoneType.Invalid;
    public int         Track          { get; set; }  = 0;

    [JsonIgnore]
    public EntityIndex Index { get; set; }

    // The box before Yaw turns it about the centre between them.
    public Vector Corner1    { get; set; } = new ();
    public Vector Corner2    { get; set; } = new ();

    // Degrees about the vertical axis, 0 to 360; a map's own zone keeps 0.
    public float  Yaw        { get; set; }

    [JsonIgnore]
    public string TargetName { get; init; } = string.Empty;

    public Vector?     TeleportOrigin { get; set; }
    public Vector?     TeleportAngles { get; set; }

    public Vector Origin { get; set; } = new ();

    [JsonIgnore]
    public Vector? GroundOrigin { get; set; }

    public bool Prebuilt { get; init; }

    public int Data { get; set; } = 0; // Stage number for Stage zones, checkpoint index for Checkpoint zones

    // Its outline, worked out once (a prebuilt zone's comes from its model), and the bottom of it.
    [JsonIgnore]
    public List<Edge>? Edges { get; set; }

    [JsonIgnore]
    public List<Edge>? FlatEdges { get; set; }
}
