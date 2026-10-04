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
using System.Text.Json;
using Cysharp.Text;
using Sharp.Shared.Definition;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer;

internal static class Utils
{
    public static readonly JsonSerializerOptions SerializerOptions = new () { WriteIndented = true, IndentSize = 4 };

    public static readonly JsonSerializerOptions DeserializerOptions = new ()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string FormatTime(float totalSeconds, bool precise = false)
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            FormatTime(ref sb, totalSeconds, precise);

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    ///     The milliseconds a time shows: truncated, like every timer, and safe for any float (0 when not finite).
    /// </summary>
    public static long Millis(float seconds)
        => float.IsFinite(seconds) ? (long) Math.Clamp(Math.Floor((seconds * 1000d) + 1e-6), -1e15, 1e15) : 0;

    /// <summary>
    ///     A time that grows with it: "5.123", then "01:05.500", then "01:02:03.450". Truncated to milliseconds when
    ///     precise, else to tenths ("5.1").
    /// </summary>
    public static void FormatTime(ref Utf16ValueStringBuilder sb, float totalSeconds, bool precise = false)
    {
        var ms = Millis(MathF.Abs(totalSeconds));

        if (totalSeconds < 0 && ms > 0)
        {
            sb.Append('-');
        }

        AppendMillis(ref sb, ms, precise);
    }

    /// <summary>
    ///     A non-negative time in whole milliseconds, as <see cref="FormatTime(ref Utf16ValueStringBuilder, float, bool)" />
    ///     shows it.
    /// </summary>
    public static void AppendMillis(ref Utf16ValueStringBuilder sb, long ms, bool precise = true)
    {
        var seconds = ms / 1000;
        var hours   = seconds / 3600;
        var minutes = (seconds / 60) % 60;

        if (hours > 0)
        {
            if (hours < 10)
            {
                sb.Append('0');
            }

            sb.Append(hours);
            sb.Append(':');
            AppendPadded2(ref sb, (int) minutes);
            sb.Append(':');
            AppendPadded2(ref sb, (int) (seconds % 60));
        }
        else if (minutes > 0)
        {
            AppendPadded2(ref sb, (int) minutes);
            sb.Append(':');
            AppendPadded2(ref sb, (int) (seconds % 60));
        }
        else
        {
            sb.Append(seconds);
        }

        sb.Append('.');

        if (precise)
            AppendPadded3(ref sb, (int) (ms % 1000));
        else
            sb.Append((char) ('0' + (ms % 1000 / 100)));
    }

    /// <summary>
    ///     A value in chat green, then back to white.
    /// </summary>
    public static string Highlight<T>(T value)
        => ZString.Concat(ChatColor.LightGreen, value, ChatColor.White);

    /// <summary>
    ///     A chat-colored (green) formatted time: <c>{green}01:05.500{white}</c>.
    /// </summary>
    public static string ColoredTime(float time, bool precise = true)
        => Highlight(FormatTime(time, precise));

    /// <summary>
    ///     A signed chat-colored time delta: red <c>+</c> when losing time,
    ///     green <c>-</c> when ahead, followed by |delta| and a reset to white.
    /// </summary>
    public static string SignedDelta(float delta, bool precise = true)
        => ZString.Concat(delta >= 0f ? ChatColor.Red : ChatColor.LightGreen,
                          delta >= 0f ? '+' : '-',
                          FormatTime(MathF.Abs(delta), precise),
                          ChatColor.White);

    private static void AppendPadded2(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    private static void AppendPadded3(ref Utf16ValueStringBuilder sb, int value)
    {
        sb.Append((char) ('0' + (value / 100)));
        sb.Append((char) ('0' + ((value / 10) % 10)));
        sb.Append((char) ('0' + (value % 10)));
    }

    public static string GetTrackName(int track, bool ignoreNumber = false)
    {
        return track switch
        {
            < 0 or >= TimerConstants.MAX_TRACK =>
                throw new IndexOutOfRangeException($"Track out of range. [0, {TimerConstants.MAX_TRACK})"),
            0                     => "Main",
            > 0 when ignoreNumber => "Bonus",
            > 0                   => $"Bonus {track}",
        };
    }
}
