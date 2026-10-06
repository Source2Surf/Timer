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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;

namespace Source2Surf.Timer.Modules.Rank;

/// <summary>
///     timer-ranks.jsonc: titles by global points rank, shown as a chat tag and the scoreboard clan tag.
/// </summary>
internal sealed class RankConfig
{
    [JsonPropertyName("titles")]
    public List<RankTitleConfig> Titles { get; set; } =
    [
        new () { Name = "Champion", MaxRank   = 1, Color  = "Gold" },
        new () { Name = "Legend", MaxRank     = 3, Color  = "Yellow" },
        new () { Name = "Master", MaxRank     = 10, Color = "LightRed" },
        new () { Name = "Elite", MaxPercent   = 1, Color  = "Purple" },
        new () { Name = "Expert", MaxPercent  = 5, Color  = "Blue" },
        new () { Name = "Skilled", MaxPercent = 20, Color = "Green" },
        new () { Name = "Ranked", Color       = "Silver" },
    ];

    // Title of players without points; empty for none.
    [JsonPropertyName("unranked")]
    public string Unranked { get; set; } = "";

    [JsonPropertyName("chat_tags")]
    public bool ChatTags { get; set; } = true;

    // A chat line: {prefix} {title} {rank} {total} {name} {message}, {color} the title's color, {team} the team color, or
    // a ChatColor name like {gold}.
    [JsonPropertyName("chat_format")]
    public string ChatFormat { get; set; } = "{prefix}{color}[{title}] {team}{name}{white}: {message}";

    // A chat line of a player without a title.
    [JsonPropertyName("chat_format_untitled")]
    public string ChatFormatUntitled { get; set; } = "{prefix}{team}{name}{white}: {message}";

    // {prefix}: what the game would put before a dead player's, a spectator's or a team-only message.
    [JsonPropertyName("chat_prefix_dead")]
    public string ChatPrefixDead { get; set; } = "{grey}*DEAD* ";

    [JsonPropertyName("chat_prefix_spec")]
    public string ChatPrefixSpec { get; set; } = "{grey}*SPEC* ";

    [JsonPropertyName("chat_prefix_team")]
    public string ChatPrefixTeam { get; set; } = "{grey}(Team) ";

    [JsonPropertyName("scoreboard_tags")]
    public bool ScoreboardTags { get; set; } = true;

    // The clan tag: {title} {rank} {total}.
    [JsonPropertyName("scoreboard_format")]
    public string ScoreboardFormat { get; set; } = "{title}";

    public static RankConfig Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<RankConfig>(File.ReadAllText(path), Utils.DeserializerOptions) is { } config)
            {
                return config;
            }

            var defaults = new RankConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Utils.SerializerOptions));

            return defaults;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read {path}, using the default ranks", path);

            return new RankConfig();
        }
    }
}

internal sealed class RankTitleConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // The title covers ranks up to this one; 0 covers every rank.
    [JsonPropertyName("max_rank")]
    public int MaxRank { get; set; }

    // Without a max_rank, the title covers the top this many percent of ranked players instead.
    [JsonPropertyName("max_percent")]
    public double MaxPercent { get; set; }

    // A ChatColor name, e.g. Gold.
    [JsonPropertyName("color")]
    public string Color { get; set; } = "White";
}

internal readonly record struct RankTitle(string Name, string Color);

/// <summary>
///     The config's titles, top one first, with their colors resolved.
/// </summary>
internal sealed class RankTitles
{
    private static readonly Dictionary<string, string> Colors =
        typeof(ChatColor).GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
                         .ToDictionary(f => f.Name, f => (string) f.GetValue(null)!, StringComparer.OrdinalIgnoreCase);

    private readonly (int MaxRank, double MaxPercent, RankTitle Title)[] _titles;
    private readonly RankTitle?                                          _unranked;

    public RankTitles(RankConfig config)
    {
        _titles = config.Titles
                        .Where(t => !string.IsNullOrWhiteSpace(t.Name))
                        .Select(t => (t.MaxRank, t.MaxPercent, new RankTitle(t.Name, Color(t.Color))))
                        .ToArray();

        _unranked = string.IsNullOrWhiteSpace(config.Unranked) ? null : new RankTitle(config.Unranked, ChatColor.Grey);
    }

    // The first title covering the rank among total ranked players.
    public RankTitle? For(int rank, int total)
    {
        if (rank <= 0)
        {
            return _unranked;
        }

        foreach (var (maxRank, maxPercent, title) in _titles)
        {
            if (maxRank > 0 ? rank <= maxRank : maxPercent <= 0 || rank <= total * maxPercent / 100)
            {
                return title;
            }
        }

        return null;
    }

    private static string Color(string name)
        => Colors.GetValueOrDefault(name, ChatColor.White);

    /// <summary>
    ///     Fills a chat_format or scoreboard_format. Unknown {tokens} are left as they are.
    /// </summary>
    public static string Render(string format, RankTitle? title, int rank, int total, string name = "", string message = "",
                                string prefix = "")
    {
        var sb = new StringBuilder(format.Length + name.Length + message.Length + prefix.Length + 16);

        for (var i = 0; i < format.Length; i++)
        {
            var end = format[i] == '{' ? format.IndexOf('}', i + 1) : -1;

            if (end < 0)
            {
                sb.Append(format[i]);

                continue;
            }

            var token = format.AsSpan(i + 1, end - i - 1);

            if (token.Equals("title", StringComparison.OrdinalIgnoreCase))
                sb.Append(title?.Name);
            else if (token.Equals("rank", StringComparison.OrdinalIgnoreCase))
                sb.Append(rank > 0 ? rank.ToString(CultureInfo.InvariantCulture) : "-");
            else if (token.Equals("total", StringComparison.OrdinalIgnoreCase))
                sb.Append(total.ToString(CultureInfo.InvariantCulture));
            else if (token.Equals("name", StringComparison.OrdinalIgnoreCase))
                sb.Append(name);
            else if (token.Equals("message", StringComparison.OrdinalIgnoreCase))
                sb.Append(message);
            else if (token.Equals("prefix", StringComparison.OrdinalIgnoreCase))
                sb.Append(prefix);
            else if (token.Equals("color", StringComparison.OrdinalIgnoreCase))
                sb.Append(title?.Color);
            else if (token.Equals("team", StringComparison.OrdinalIgnoreCase))
                sb.Append(ChatColor.Head);
            else if (Colors.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(token, out var color))
                sb.Append(color);
            else
            {
                sb.Append(format, i, end - i + 1);
            }

            i = end;
        }

        return sb.ToString();
    }
}
