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
using System.Linq;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A player's settings as the backend keeps them (the HUD's, !hide, the finish sounds): a few bytes, only what
///     differs from the defaults.
///     <code>
///     [version 1]
///     [n] n × (option key, choice index)   options not at their initial choice
///     [m] m × line                         the timer's line order; 0 for the default order
///     [k] k × (panel key, x, y)            placed panels, x/y as shown (-50..50, signed)
///     </code>
///     All defaults encode as no bytes. Reading is forgiving: an unknown version reads as defaults, unknown keys and
///     out-of-range values are skipped, and a line list that isn't a full order reads as the default order.
/// </summary>
internal static class PlayerSettingsCodec
{
    public const byte Version = 1;

    /// <summary>
    ///     The backend refuses more.
    /// </summary>
    public const int MaxLength = 256;

    public static byte[] Encode(HudPlayer p)
    {
        var options = new List<HudOption>();

        foreach (var option in HudOptions.All)
        {
            if (p.Settings[option.Index] != option.Initial)
            {
                options.Add(option);
            }
        }

        var reordered = !p.Order.SequenceEqual(HudLines.DefaultOrder);

        // A panel on its way back to its CSS layout is left out.
        var placed = new List<HudTarget>();

        foreach (var target in HudTargets.All)
        {
            if (p.Positions[(int) target] is not null && float.IsNaN(p.UnplaceAt[(int) target]))
            {
                placed.Add(target);
            }
        }

        if (options.Count == 0 && !reordered && placed.Count == 0)
        {
            return [];
        }

        var data = new List<byte>(4 + (options.Count * 2) + HudLines.Count + (placed.Count * 3)) { Version, (byte) options.Count };

        foreach (var option in options)
        {
            data.Add(option.Key);
            data.Add((byte) p.Settings[option.Index]);
        }

        data.Add((byte) (reordered ? p.Order.Length : 0));

        if (reordered)
        {
            foreach (var line in p.Order)
            {
                data.Add((byte) line);
            }
        }

        data.Add((byte) placed.Count);

        foreach (var target in placed)
        {
            var pos = p.Positions[(int) target]!;
            data.Add(HudTargets.Def(target).Key);
            data.Add(unchecked((byte) (sbyte) HudFormat.ShownOffset(pos.X, pos.Cx)));
            data.Add(unchecked((byte) (sbyte) HudFormat.ShownOffset(pos.Y, pos.Cy)));
        }

        return data.ToArray();
    }

    /// <summary>
    ///     Applies saved settings over the defaults. The caller clamps the positions.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> data, HudPlayer p)
    {
        p.ResetSettings();

        if (data.Length == 0 || data[0] != Version)
        {
            return;
        }

        var at = 1;

        // A short read stops there, keeping what came before.
        if (!TryRead(data, ref at, out var n))
        {
            return;
        }

        for (var i = 0; i < n; i++)
        {
            if (!TryRead(data, ref at, out var key) || !TryRead(data, ref at, out var choice))
            {
                return;
            }

            if (HudOptions.ByKey.TryGetValue(key, out var option) && choice < option.Choices.Length)
            {
                p.Settings[option.Index] = choice;
            }
        }

        if (!TryRead(data, ref at, out var m) || at + m > data.Length)
        {
            return;
        }

        if (ReadOrder(data.Slice(at, m)) is { } order)
        {
            p.Order = order;
        }

        at += m;

        if (!TryRead(data, ref at, out var k))
        {
            return;
        }

        for (var i = 0; i < k; i++)
        {
            if (!TryRead(data, ref at, out var key) || !TryRead(data, ref at, out var x) || !TryRead(data, ref at, out var y))
            {
                return;
            }

            if (HudTargets.ByKey.TryGetValue(key, out var target))
            {
                p.Positions[(int) target] = new HudPosition
                {
                    X = Math.Clamp((int) (sbyte) x, -50, 50),
                    Y = Math.Clamp((int) (sbyte) y, -50, 50),
                };
            }
        }
    }

    // Every line once, or nothing.
    private static HudLine[]? ReadOrder(ReadOnlySpan<byte> lines)
    {
        if (lines.Length != HudLines.Count)
        {
            return null;
        }

        var order = new HudLine[lines.Length];
        var seen  = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i] >= HudLines.Count || (seen & (1 << lines[i])) != 0)
            {
                return null;
            }

            seen     |= 1 << lines[i];
            order[i] =  (HudLine) lines[i];
        }

        return order;
    }

    private static bool TryRead(ReadOnlySpan<byte> data, ref int at, out byte value)
    {
        if (at >= data.Length)
        {
            value = 0;

            return false;
        }

        value = data[at++];

        return true;
    }
}
