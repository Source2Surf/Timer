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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer.Modules.MapInfo;

/// <summary>
///     One game mode in timer-gamemodes.jsonc: the maps it covers and their defaults, which map and style settings
///     override.
/// </summary>
internal sealed record GameModeConfig
{
    // Maps whose name starts with this, in any case.
    [JsonPropertyName("prefix")]
    public string Prefix { get; init; } = "";

    // surf, bhop or none.
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "none";

    // In map_configs, run on each of its maps; written with the base cvars and these the first time.
    [JsonPropertyName("cfg")]
    public string Cfg { get; init; } = "";

    [JsonPropertyName("cvars")]
    public string[] Cvars { get; init; } = [];

    [JsonPropertyName("enter_speed_limit")]
    public float EnterSpeedLimit { get; init; } = 260f;

    [JsonPropertyName("exit_speed_limit")]
    public float ExitSpeedLimit { get; init; } = 375f;

    [JsonPropertyName("max_prejumps")]
    public int MaxPrejumps { get; init; } = 1;

    [JsonPropertyName("airaccelerate")]
    public float AirAccelerate { get; init; } = 150f;

    // sv_air_max_wishspeed for styles that don't set their own.
    [JsonPropertyName("wishspeed")]
    public float WishSpeed { get; init; } = 30f;

    [JsonIgnore]
    public EGameMode GameMode
        => Enum.TryParse<EGameMode>(Mode, true, out var mode) ? mode : EGameMode.None;
}

internal sealed record GameModesConfig
{
    [JsonPropertyName("modes")]
    public GameModeConfig[] Modes { get; init; } =
    [
        new () { Prefix = "surf", Mode = "surf", Cfg = "surf.cfg", Cvars = ["sv_airaccelerate 150"], ExitSpeedLimit = 375f, AirAccelerate = 150f },
        new () { Prefix = "bhop", Mode = "bhop", Cfg = "bhop.cfg", Cvars = ["sv_airaccelerate 1000"], ExitSpeedLimit = 290f, AirAccelerate = 1000f },
    ];

    // Maps no mode's prefix matches.
    [JsonPropertyName("default")]
    public GameModeConfig Default { get; init; } = new ();

    // The first mode whose prefix the map's name starts with.
    public GameModeConfig For(string mapName)
        => Modes.FirstOrDefault(m => m.Prefix.Length > 0 && mapName.StartsWith(m.Prefix, StringComparison.OrdinalIgnoreCase))
           ?? Default;

    public static GameModesConfig Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<GameModesConfig>(File.ReadAllText(path), Utils.DeserializerOptions) is { } config)
            {
                return config;
            }

            var defaults = new GameModesConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Utils.SerializerOptions));

            return defaults;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read {path}, using the default game modes", path);

            return new GameModesConfig();
        }
    }
}
