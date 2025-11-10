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

using System;
using System.Text.Json;

namespace SurfTimer;

internal static class Utils
{
    public const float TickInterval = 1 / 64f;
    public const int   Tickrate     = 64;
    public const int   MAX_STYLE    = 16;
    public const int   MAX_TRACK    = 32;
    public const int   MAX_STAGE    = 64;

    public static readonly JsonSerializerOptions SerializerOptions = new ()
    {
        WriteIndented = true,
        IndentSize    = 4,
    };

    public static readonly JsonSerializerOptions DeserializerOptions = new ()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string FormatTime(float totalSeconds, bool precise = false)
    {
        var negative = totalSeconds < 0;
        var time     = TimeSpan.FromSeconds(Math.Abs(totalSeconds));

        var ms = precise
            ? time.Milliseconds.ToString("D3")
            : (time.Milliseconds / 100).ToString("D1");

        var formatted = time.TotalHours >= 1.0
            ? $"{(int) time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}.{ms}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}.{ms}";

        return negative ? "-" + formatted : formatted;
    }

    public static string GetTrackName(int track, bool ignoreNumber = false)
    {
        return track switch
        {
            < 0 or >= MAX_TRACK   => throw new IndexOutOfRangeException($"Track out of range. [0, {MAX_TRACK})"),
            0                     => "Main",
            > 0 when ignoreNumber => "Bonus",
            > 0                   => $"Bonus {track}",
        };
    }
}
