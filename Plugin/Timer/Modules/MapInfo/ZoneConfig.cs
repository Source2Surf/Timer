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
 
using System.Text.Json.Serialization;

namespace Source2Surf.Timer.Modules.MapInfo;

internal record ZoneConfig
{
    /// <summary>
    /// Speed cap override when entering the start zone. null = no override (falls back to GameMode default), 0 or negative = unlimited.
    /// </summary>
    [JsonPropertyName("enter_speed_limit")]
    public float? EnterSpeedLimit { get; set; } = null;

    /// <summary>
    /// Speed cap override when leaving the start zone. null = no override (falls back to Style/GameMode default), 0 or negative = unlimited.
    /// </summary>
    [JsonPropertyName("exit_speed_limit")]
    public float? ExitSpeedLimit { get; set; } = null;

    [JsonPropertyName("max_jumps")]
    public int? MaxJumps { get; set; } = null;

    /// <summary>
    /// Optional overrides for stage zones on this track.
    /// When set, stage zone enter/exit speed limits use these values instead of the track defaults.
    /// </summary>
    [JsonPropertyName("stage_zone")]
    public ZoneConfig? StageZone { get; set; } = null;
}
