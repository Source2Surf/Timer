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

namespace SurfTimer.Modules.Style;

internal record StyleSetting
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Normal";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "normal;n";

    [JsonPropertyName("autobhop")]
    public bool AutoBhop { get; init; } = true;

    [JsonPropertyName("allow_bunnyhopping")]
    public bool AllowBunnyhopping { get; init; } = true;

    [JsonPropertyName("custom_airaccelerate")]
    public bool CustomAirAccelerate { get; init; } = false;

    [JsonPropertyName("airaccelerate")]
    public float AirAccelerate { get; init; } = 150.0f;

    [JsonPropertyName("accelerate")]
    public float Accelerate { get; init; } = 5.0f;

    [JsonPropertyName("friction")]
    public float Friction { get; init; } = 4.0f;

    [JsonPropertyName("wishspeed")]
    public float WishSpeed { get; init; } = 30.0f;

    [JsonPropertyName("runspeed")]
    public float RunSpeed { get; init; } = 260.0f;

    [JsonPropertyName("block_w")]
    public bool BlockW { get; init; } = false;

    [JsonPropertyName("block_s")]
    public bool BlockS { get; init; } = false;

    [JsonPropertyName("block_a")]
    public bool BlockA { get; init; } = false;

    [JsonPropertyName("block_d")]
    public bool BlockD { get; init; } = false;
}