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
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Sharp.Shared.Enums;

namespace Timer.MapChooser;

internal sealed class MapChooserConfig
{
    private static readonly JsonSerializerOptions Options = new ()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling  = JsonCommentHandling.Skip,
        AllowTrailingCommas  = true,
    };

    private const string Template = """
                                    {
                                        // How long a map runs, in minutes.
                                        "time_limit": 30,
                                        // Seconds before the end that the vote for the next map starts.
                                        "vote_before_end": 180,
                                        "vote_duration": 20,
                                        // Maps in a vote; nominations fill them first.
                                        "vote_maps": 5,
                                        "max_extends": 2,
                                        "extend_minutes": 10,
                                        // Share of players needed to rock the vote, and how long into a map before they can.
                                        "rtv_ratio": 0.6,
                                        "rtv_delay": 120,
                                        // Seconds between announcing the next map and changing to it.
                                        "change_delay": 5,
                                        // The last maps played (this one included) that can't come up again.
                                        "recent_maps": 5,
                                        // Local maps to offer besides the server's workshop maps, and maps never to offer.
                                        "extra_maps": [],
                                        "exclude": [],
                                        // Keys for the vote: commands to move up and down, and a button to vote.
                                        "key_up": "autobuy",
                                        "key_down": "rebuy",
                                        "key_select": "LookAtWeapon"
                                    }
                                    """;

    public float        TimeLimit     { get; set; } = 30;
    public float        VoteBeforeEnd { get; set; } = 180;
    public float        VoteDuration  { get; set; } = 20;
    public int          VoteMaps      { get; set; } = 5;
    public int          MaxExtends    { get; set; } = 2;
    public int          ExtendMinutes { get; set; } = 10;
    public float        RtvRatio      { get; set; } = 0.6f;
    public float        RtvDelay      { get; set; } = 120;
    public float        ChangeDelay   { get; set; } = 5;
    public int          RecentMaps    { get; set; } = 5;
    public List<string> ExtraMaps     { get; set; } = [];
    public List<string> Exclude       { get; set; } = [];
    public string       KeyUp         { get; set; } = "autobuy";
    public string       KeyDown       { get; set; } = "rebuy";
    public string       KeySelect     { get; set; } = "LookAtWeapon";

    public UserCommandButtons SelectButton
        => Enum.TryParse<UserCommandButtons>(KeySelect, true, out var button) ? button : UserCommandButtons.LookAtWeapon;

    /// <summary>
    ///     The command bound to <see cref="SelectButton" />'s key, to look the key up.
    /// </summary>
    public string SelectCommand
        => SelectButton switch
        {
            UserCommandButtons.Attack     => "attack",
            UserCommandButtons.Attack2    => "attack2",
            UserCommandButtons.Jump       => "jump",
            UserCommandButtons.Duck       => "duck",
            UserCommandButtons.Use        => "use",
            UserCommandButtons.Reload     => "reload",
            UserCommandButtons.Speed      => "sprint",
            UserCommandButtons.Scoreboard => "showscores",
            UserCommandButtons.Zoom       => "zoom",
            _                             => "lookatweapon",
        };

    /// <summary>
    ///     Reads the config, writing the default one first if there is none.
    /// </summary>
    public static MapChooserConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Template);
        }

        var config = JsonSerializer.Deserialize<MapChooserConfig>(File.ReadAllText(path), Options) ?? new MapChooserConfig();
        config.Clamp();

        return config;
    }

    private void Clamp()
    {
        TimeLimit     = Math.Max(1, TimeLimit);
        VoteDuration  = Math.Clamp(VoteDuration, 5, 120);
        VoteBeforeEnd = Math.Clamp(VoteBeforeEnd, VoteDuration + 5, TimeLimit * 60);
        VoteMaps      = Math.Clamp(VoteMaps, 2, 8);
        MaxExtends    = Math.Max(0, MaxExtends);
        ExtendMinutes = Math.Max(1, ExtendMinutes);
        RtvRatio      = Math.Clamp(RtvRatio, 0.01f, 1f);
        RtvDelay      = Math.Max(0, RtvDelay);
        ChangeDelay   = Math.Clamp(ChangeDelay, 1, 30);
        RecentMaps    = Math.Max(1, RecentMaps);
    }
}
