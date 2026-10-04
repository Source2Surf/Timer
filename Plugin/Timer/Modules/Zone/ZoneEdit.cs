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
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Zone;

// A zone as the zone editor lists it. Number is the stage or checkpoint number, 0 for other types.
internal readonly record struct ZoneEntry(uint Id, int Track, EZoneType Type, int Number, bool Prebuilt);

// A zone a player is placing. Step 0 waits for the first corner, 1 for the second.
internal readonly record struct ZoneBuildState(int Track, EZoneType Type, int Number, int Step);

internal static class ZoneEdit
{
    public const int MaxNumber = 99;

    // Stage 1 is the start zone, so stages count from 2.
    public static int FirstNumber(EZoneType type)
        => type switch
        {
            EZoneType.Stage      => 2,
            EZoneType.Checkpoint => 1,
            _                    => 0,
        };

    public static bool IsNumbered(EZoneType type)
        => type is EZoneType.Stage or EZoneType.Checkpoint;

    public static bool IsValid(int track, EZoneType type, int number)
        => track is >= 0 and < TimerConstants.MAX_TRACK
           && type is > EZoneType.Invalid and < EZoneType.Max
           && (IsNumbered(type) ? number >= FirstNumber(type) && number <= MaxNumber : number == 0);

    public static int NextNumber(IEnumerable<ZoneEntry> zones, int track, EZoneType type)
    {
        if (!IsNumbered(type))
        {
            return 0;
        }

        var next = FirstNumber(type);

        foreach (var zone in zones)
        {
            if (zone.Track == track && zone.Type == type && zone.Number >= next)
            {
                next = zone.Number + 1;
            }
        }

        return next;
    }

    // Main track first, then bonuses; in a track: start, stages, checkpoints, end, stop timer.
    public static int Compare(ZoneEntry a, ZoneEntry b)
    {
        var c = a.Track.CompareTo(b.Track);

        if (c == 0)
        {
            c = TypeOrder(a.Type).CompareTo(TypeOrder(b.Type));
        }

        if (c == 0)
        {
            c = a.Number.CompareTo(b.Number);
        }

        if (c == 0)
        {
            c = b.Prebuilt.CompareTo(a.Prebuilt);
        }

        return c != 0 ? c : a.Id.CompareTo(b.Id);
    }

    private static int TypeOrder(EZoneType type)
        => type switch
        {
            EZoneType.Start      => 0,
            EZoneType.Stage      => 1,
            EZoneType.Checkpoint => 2,
            EZoneType.End        => 3,
            _                    => 4,
        };

    // "start", "stage 3", "b1 end", "b1 checkpoint 2". Number is null when not given.
    public static bool TryParse(IReadOnlyList<string> args, out int track, out EZoneType type, out int? number)
    {
        track  = 0;
        type   = EZoneType.Invalid;
        number = null;

        var i = 0;

        if (args.Count > 0 && args[0].Length > 1 && args[0][0] is 'b' or 'B')
        {
            if (!int.TryParse(args[0].AsSpan(1), out track) || track < 1 || track >= TimerConstants.MAX_TRACK)
            {
                return false;
            }

            i++;
        }

        if (i >= args.Count
            || args[i].Length == 0
            || char.IsDigit(args[i][0])
            || !Enum.TryParse(args[i], true, out type)
            || type is EZoneType.Invalid or EZoneType.Max)
        {
            return false;
        }

        i++;

        if (i < args.Count)
        {
            if (!IsNumbered(type) || !int.TryParse(args[i], out var n))
            {
                return false;
            }

            number = n;
        }

        return number is not { } given || IsValid(track, type, given);
    }
}
