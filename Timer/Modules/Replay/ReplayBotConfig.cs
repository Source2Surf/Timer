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
 
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SurfTimer.Modules.Replay;

internal enum EReplayBotType : byte
{
    Looping,
}

internal enum EReplayBotPlayType : byte
{
    All,
    MainOnly,
    BonusOnly,
}

internal record ReplayBotConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("type")]
    public EReplayBotType Type { get; init; } = EReplayBotType.Looping;

    [JsonPropertyName("play_type")]
    public EReplayBotPlayType PlayType { get; init; } = EReplayBotPlayType.All;

    [JsonPropertyName("styles")]
    public List<int> Styles { get; set; } = [0];

    [JsonPropertyName("stage_bot")]
    public bool StageBot { get; init; } = false;

    [JsonPropertyName("idle_name")]
    public string IdleName { get; init; } = "{track}{stage} Replay Bot";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "{track}{stage} - {style} - {time}";
}
