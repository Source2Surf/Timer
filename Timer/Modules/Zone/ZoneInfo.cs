/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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

namespace SurfTimer.Modules.Zone;

internal enum EZoneType : sbyte
{
    Invalid    = -1,
    Start      = 0,
    End        = 1,
    Stage      = 2,
    Checkpoint = 3,
    StopTimer  = 4,
}

internal class ZoneInfo
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

    public int Data { get; set; } = 0; // 如果是Stage的就是第几关, Checkpoint就是第几个Checkpoint

    [JsonIgnore]
    public IBaseEntity[]? Beams = null; // 只有 ZoneType 为 Start 和 End 的时候才有
}
