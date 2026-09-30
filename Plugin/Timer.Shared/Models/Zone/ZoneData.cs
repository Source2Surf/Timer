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

using Sharp.Shared.Types;

namespace Source2Surf.Timer.Shared.Models.Zone;

public sealed class ZoneData
{
    public ulong     Id             { get; set; }
    public EZoneType Type           { get; set; }
    public int       Track          { get; set; }
    public int       Sequence       { get; set; }
    public Vector    Mins           { get; set; }
    public Vector    Maxs           { get; set; }
    public Vector    Center         { get; set; }
    public Vector?   TeleportOrigin { get; set; }
    public Vector?   TeleportAngles { get; set; }
    public string?   Config         { get; set; }
}
