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

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Source2Surf.Timer.Shared.Models.Style;

public record StyleSetting
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "Normal";

    [JsonPropertyName("command")]
    public string Command { get; init; } = "normal;n";

    /// <summary>
    /// What the style is about, for the style list; may use {colour} chat tags.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    /// <summary>
    /// Runs and replays are stored under this, so it stays with the style however the list changes: 0-63. Without one
    /// (-1), a style's id is its place in the list.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; } = -1;

    /// <summary>
    /// A disabled style can't be picked and is left out of the menus; its runs are kept.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("autobhop")]
    public bool AutoBhop { get; init; } = true;

    [JsonPropertyName("allow_bunnyhopping")]
    public bool AllowBunnyhopping { get; init; } = true;

    [JsonPropertyName("custom_airaccelerate")]
    public bool CustomAirAccelerate { get; init; } = false;

    [JsonPropertyName("airaccelerate")]
    public float AirAccelerate { get; init; } = 150.0f;

    /// <summary>
    /// Whether to use a custom pre-speed cap. When true, the PreSpeed value overrides the game mode default.
    /// StyleSetting is server-authoritative data defined in the server's styles.jsonc; players cannot modify it.
    /// </summary>
    [JsonPropertyName("custom_prespeed")]
    public bool CustomPreSpeed { get; init; } = false;

    [JsonPropertyName("prespeed")]
    public float PreSpeed { get; init; } = 375.0f;

    /// <summary>
    /// Start-zone jump limit override, under a map's max_jumps; <see cref="Prejumps" /> -1 is no limit.
    /// </summary>
    [JsonPropertyName("custom_prejumps")]
    public bool CustomPrejumps { get; init; } = false;

    [JsonPropertyName("prejumps")]
    public int Prejumps { get; init; } = 1;

    /// <summary>
    /// Half-sideways, as bhoptimer's force_hsw: 1 = only W+A or W+D count; 2 = surf HSW, W+A/S+D or W+D/S+A, whichever
    /// pair the player uses first after leaving the start zone.
    /// </summary>
    [JsonPropertyName("force_hsw")]
    public int ForceHsw { get; init; }

    /// <summary>
    /// Only A or only D, whichever the player uses first in the run.
    /// </summary>
    [JsonPropertyName("a_or_d_only")]
    public bool AOrDOnly { get; init; }

    /// <summary>
    /// Input only counts while the player looks back along where they move, as shavit-style-backwards.
    /// </summary>
    [JsonPropertyName("force_backwards")]
    public bool ForceBackwards { get; init; }

    /// <summary>
    /// Holds force_hsw, a_or_d_only and force_backwards on the ground too, not just in the air.
    /// </summary>
    [JsonPropertyName("force_groundkeys")]
    public bool ForceGroundKeys { get; init; }

    [JsonPropertyName("gravity")]
    public float Gravity { get; init; } = 1f;

    /// <summary>
    /// Movement speed (the player's lagged movement); the timer runs as usual.
    /// </summary>
    [JsonPropertyName("speed")]
    public float Speed { get; init; } = 1f;

    /// <summary>
    /// Like speed, but the timer runs at it too: a 0.5 run counts every other tick.
    /// </summary>
    [JsonPropertyName("timescale")]
    public float Timescale { get; init; } = 1f;

    // Each jump, as bhoptimer's: horizontal speed times "velocity", plus "bonus_velocity", at least "min_velocity", at
    // most "velocity_limit"; vertical speed times "jump_multiplier", plus "jump_bonus". 0 leaves each out.
    [JsonPropertyName("velocity_limit")]
    public float VelocityLimit { get; init; }

    [JsonPropertyName("velocity")]
    public float VelocityMultiplier { get; init; } = 1f;

    [JsonPropertyName("bonus_velocity")]
    public float BonusVelocity { get; init; }

    [JsonPropertyName("min_velocity")]
    public float MinVelocity { get; init; }

    [JsonPropertyName("jump_multiplier")]
    public float JumpMultiplier { get; init; }

    [JsonPropertyName("jump_bonus")]
    public float JumpBonus { get; init; }

    /// <summary>
    /// The timescale the timer can use: out of (0, 10] is 1.
    /// </summary>
    [JsonIgnore]
    public float TimerScale => Timescale is > 0f and <= 10f ? Timescale : 1f;

    [JsonIgnore]
    public bool ChangesJumps
        => VelocityLimit > 0f || (VelocityMultiplier != 1f && VelocityMultiplier != 0f) || BonusVelocity != 0f || MinVelocity > 0f
           || JumpMultiplier != 0f || JumpBonus != 0f;

    [JsonPropertyName("accelerate")]
    public float Accelerate { get; init; } = 5.0f;

    [JsonPropertyName("friction")]
    public float Friction { get; init; } = 4.0f;

    // sv_air_max_wishspeed; null is the game mode's (timer-gamemodes.jsonc).
    [JsonPropertyName("wishspeed")]
    public float? WishSpeed { get; init; }

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

    /// <summary>
    /// Segmented style: players may save and teleport back to their own locations during a run and the run still
    /// counts for records. On any other style, teleporting to a saved location turns the run into practice.
    /// </summary>
    [JsonPropertyName("segmented")]
    public bool Segmented { get; init; } = false;

    /// <summary>
    /// Score multiplier for this style.
    /// Defaults to 1.0. Set to 1.5 for a 1.5x score multiplier, or 0 to exclude this style from scoring.
    /// </summary>
    [JsonPropertyName("score_factor")]
    public double ScoreFactor { get; init; } = 1.0;

    /// <summary>
    /// Keys this record doesn't define, kept for other modules to read.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }
}
