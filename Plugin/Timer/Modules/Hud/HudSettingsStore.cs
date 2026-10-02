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
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A player's HUD settings as saved: option labels by option id, the timer's line order, and dragged panels'
///     positions by panel. Unknown or invalid entries are ignored on load, so the format can grow.
/// </summary>
internal sealed class HudSavedSettings
{
    public Dictionary<string, string>           Options   { get; set; } = [];
    public List<string>                         LineOrder { get; set; } = [];
    public Dictionary<string, HudSavedPosition> Placement { get; set; } = [];
}

internal sealed class HudSavedPosition
{
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
///     Keeps each player's HUD settings in <c>{data}/hud/{steamid64}.json</c>. A small file per player, read once
///     when they join and written in the background shortly after they change something. Kept behind this class
///     so a database store can take its place.
/// </summary>
internal sealed class HudSettingsStore
{
    private readonly string  _directory;
    private readonly ILogger _logger;

    public HudSettingsStore(string dataPath, ILogger logger)
    {
        _directory = Path.Combine(dataPath, "hud");
        _logger    = logger;

        Directory.CreateDirectory(_directory);
    }

    public HudSavedSettings? Load(ulong steamId)
    {
        var path = PathOf(steamId);

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<HudSavedSettings>(File.ReadAllText(path), Utils.DeserializerOptions)
                : null;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to read HUD settings from {path}", path);

            return null;
        }
    }

    /// <summary>
    ///     Serializes now (on the caller's thread, so the settings can't change underneath) and writes in the
    ///     background, through a temporary file so a crash never leaves half a file.
    /// </summary>
    public void Save(ulong steamId, HudSavedSettings settings, bool wait = false)
    {
        var path = PathOf(steamId);
        var json = JsonSerializer.Serialize(settings, Utils.SerializerOptions);

        var write = Task.Run(() =>
        {
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";

            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to write HUD settings to {path}", path);

                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // nothing more to do
                }
            }
        });

        if (wait)
        {
            write.Wait();
        }
    }

    private string PathOf(ulong steamId)
        => Path.Combine(_directory, $"{steamId}.json");

    /// <summary>
    ///     Applies saved settings over the defaults. The caller clamps the positions.
    /// </summary>
    public static void Apply(HudSavedSettings saved, HudPlayer p)
    {
        p.ResetSettings();

        foreach (var option in HudOptions.All)
        {
            if (!saved.Options.TryGetValue(option.Id, out var label))
            {
                continue;
            }

            var index = Array.FindIndex(option.Choices, c => c.Label == label);

            if (index >= 0)
            {
                p.Settings[option.Index] = index;
            }
        }

        if (HudLines.Parse(saved.LineOrder) is { } order)
        {
            p.Order = order;
        }

        foreach (var target in HudTargets.All)
        {
            if (saved.Placement.TryGetValue(HudTargets.Def(target).SaveKey, out var pos))
            {
                p.Positions[(int) target] = new HudPosition { X = pos.X, Y = pos.Y };
            }
        }
    }

    /// <summary>
    ///     What gets saved: rounded positions, snapped axes at 0. A panel on its way back to its CSS layout is left
    ///     out.
    /// </summary>
    public static HudSavedSettings Capture(HudPlayer p)
        => new ()
        {
            Options   = HudOptions.All.ToDictionary(o => o.Id, o => o.Choices[p.Settings[o.Index]].Label),
            LineOrder = p.Order.Select(l => l.ToString()).ToList(),
            Placement = HudTargets.All
                                  .Where(t => p.Positions[(int) t] is not null && float.IsNaN(p.UnplaceAt[(int) t]))
                                  .ToDictionary(t => HudTargets.Def(t).SaveKey,
                                                t =>
                                                {
                                                    var pos = p.Positions[(int) t]!;

                                                    return new HudSavedPosition
                                                    {
                                                        X = HudFormat.ShownOffset(pos.X, pos.Cx),
                                                        Y = HudFormat.ShownOffset(pos.Y, pos.Cy),
                                                    };
                                                }),
        };
}
