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
using Cysharp.Text;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     The HUD's formatting and layout arithmetic, free of game state so it can be tested on its own.
/// </summary>
internal static class HudFormat
{
    private const float SnapCatch   = 1.5f; // a panel's centre within this many percent of the centre line snaps onto it
    private const float SnapRelease = 3f;   // ...and has to move this far away to let go, so it doesn't flicker

    /// <summary>
    ///     The centiseconds a time shows: truncated, like every timer.
    /// </summary>
    public static long Centis(float seconds)
        => float.IsFinite(seconds) ? (long) Math.Clamp(Math.Floor((seconds * 100d) + 1e-6), -1e15, 1e15) : 0;

    /// <summary>
    ///     <paramref name="a" /> - <paramref name="b" /> in seconds, taken from the centiseconds each shows, so a
    ///     difference always agrees with the two times on screen.
    /// </summary>
    public static float DiffTime(float a, float b)
        => (Centis(a) - Centis(b)) / 100f;

    /// <summary>
    ///     MM:SS:cc, or HH:MM:SS:cc past an hour.
    /// </summary>
    public static string FormatTime(float seconds)
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            AppendTime(ref sb, seconds);

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    ///     "+00:01:50" / "-00:00:40".
    /// </summary>
    public static string FormatDiff(float seconds)
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            sb.Append(seconds < 0 ? '-' : '+');
            AppendTime(ref sb, Math.Abs(seconds));

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    ///     "+12 u/s" / "-9 u/s".
    /// </summary>
    public static string FormatSpeedDiff(float difference)
    {
        var n = RoundSpeed(difference);

        return n < 0 ? ZString.Concat('-', -n, " u/s") : ZString.Concat('+', n, " u/s");
    }

    /// <summary>
    ///     The speed colour: 1 (gain) when the shown speed went up since the last refresh, -1 (loss) when it went
    ///     down, 0 when it's unchanged. It follows the number on screen, so it behaves the same at any speed; a fixed
    ///     u/s threshold missed high speeds, where each refresh gains only a little.
    /// </summary>
    public static int SpeedTrend(float previous, float current)
        => Math.Sign(RoundSpeed(current) - RoundSpeed(previous));

    /// <summary>
    ///     A speed as a whole number, with a non-finite reading shown as 0 rather than thrown on.
    /// </summary>
    public static int RoundSpeed(float speed)
        => float.IsFinite(speed) ? (int) Math.Clamp(MathF.Round(speed), -1_000_000f, 1_000_000f) : 0;

    private static void AppendTime(ref Utf16ValueStringBuilder sb, float seconds)
    {
        var cs    = Math.Max(0, Centis(seconds));
        var hours = cs / 360000;

        if (hours > 0)
        {
            AppendPadded2(ref sb, hours);
            sb.Append(':');
        }

        AppendPadded2(ref sb, (cs / 6000) % 60);
        sb.Append(':');
        AppendPadded2(ref sb, (cs / 100) % 60);
        sb.Append(':');
        AppendPadded2(ref sb, cs % 100);
    }

    private static void AppendPadded2(ref Utf16ValueStringBuilder sb, long value)
    {
        if (value < 10)
        {
            sb.Append('0');
        }

        sb.Append(value);
    }

    // ------------------------------------------------------------------ positions

    /// <summary>
    ///     Whether a centre offset (percent of the screen) snaps onto the centre line.
    /// </summary>
    public static bool SnapsToCentre(float offset, bool wasSnapped)
        => MathF.Abs(offset) < (wasSnapped ? SnapRelease : SnapCatch);

    /// <summary>
    ///     The offset the player sees: whole percents, a snapped axis exactly on the centre line.
    /// </summary>
    public static int ShownOffset(float offset, bool snapped)
        => snapped || !float.IsFinite(offset) ? 0 : (int) MathF.Round(Math.Clamp(offset, -50f, 50f));

    /// <summary>
    ///     An offset of -50..50 as the two classes that carry it (hud_positions.css): tens on the panel as
    ///     mx-/my-<c>tens</c> ("m4" for -40), units 0-9 on its full-screen wrapper as fx-/fy-<c>units</c>.
    ///     -37 is m4 + 3.
    /// </summary>
    public static (string Tens, string Units) SplitOffset(int offset)
    {
        var tens  = (int) Math.Floor(offset / 10d);
        var units = offset - (10 * tens);

        return (tens < 0 ? ZString.Concat('m', -tens) : tens.ToString(), units.ToString());
    }

    /// <summary>
    ///     Keeps a centre offset on screen: up to <paramref name="limit" /> percent either way.
    /// </summary>
    public static float ClampAbs(float value, float limit)
        => Math.Clamp(value, -limit, limit);

    // ------------------------------------------------------------------ timer lines

    /// <summary>
    ///     Whether the blank line at <paramref name="gapAt" /> shows: only between two lines that do.
    /// </summary>
    public static bool GapShown(IReadOnlyList<bool> shown, int gapAt)
    {
        var before = false;
        var after  = false;

        for (var i = 0; i < shown.Count; i++)
        {
            if (i < gapAt)
            {
                before |= shown[i];
            }
            else if (i > gapAt)
            {
                after |= shown[i];
            }
        }

        return before && after;
    }

    /// <summary>
    ///     Which heading spacers show: a spacer follows its group when that group shows and any later one does.
    ///     <paramref name="groups" /> ends with the lines under the heading, which have no spacer of their own.
    /// </summary>
    public static bool[] SpacersShown(IReadOnlyList<bool> groups)
    {
        var spacers = new bool[Math.Max(0, groups.Count - 1)];

        for (var i = 0; i < spacers.Length; i++)
        {
            if (!groups[i])
            {
                continue;
            }

            for (var j = i + 1; j < groups.Count; j++)
            {
                if (groups[j])
                {
                    spacers[i] = true;

                    break;
                }
            }
        }

        return spacers;
    }
}
