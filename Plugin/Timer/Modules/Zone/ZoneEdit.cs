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
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules.Zone;

// A zone as the zone editor lists it. Number is the stage or checkpoint number, 0 for other types.
internal readonly record struct ZoneEntry(uint Id, int Track, EZoneType Type, int Number, bool Prebuilt);

// A zone a player is placing. Step 0 waits for the first corner, 1 for the second.
internal readonly record struct ZoneBuildState(int Track, EZoneType Type, int Number, int Step);

// Where a zone placed in game is: the centre of its box and its turn about the vertical axis.
internal readonly record struct ZonePlacement(Vector Center, float Yaw);

internal static class ZoneEdit
{
    public const int MaxNumber = TimerConstants.MAX_STAGE - 1;

    // The editor's steps: units a move, degrees a turn.
    public static readonly int[] MoveSteps = [1, 2, 4, 8, 16, 32, 64, 128];
    public static readonly int[] TurnSteps = [1, 5, 15, 45, 90];

    public const int DefaultMoveStep = 3; // 8 units
    public const int DefaultTurnStep = 2; // 15 degrees

    // 0 up to 360; anything not a number is no turn.
    public static float NormalizeYaw(float yaw)
    {
        if (!float.IsFinite(yaw))
        {
            return 0f;
        }

        yaw %= 360f;

        return yaw < 0f ? yaw + 360f : yaw;
    }

    // The point turned by degrees about the vertical axis through the pivot; positive is anticlockwise from above.
    public static Vector Turn(Vector point, Vector pivot, float degrees)
    {
        var (sin, cos) = MathF.SinCos(degrees * (MathF.PI / 180f));
        var dx         = point.X - pivot.X;
        var dy         = point.Y - pivot.Y;

        return new Vector(pivot.X + (dx * cos) - (dy * sin), pivot.Y + (dx * sin) + (dy * cos), point.Z);
    }

    // The box's 8 corners, turned about its centre: the bottom 4, then the top 4, each going round.
    public static Vector[] Corners(Vector corner1, Vector corner2, float yaw)
    {
        var center = (corner1 + corner2) / 2f;
        var low    = MathF.Min(corner1.Z, corner2.Z);
        var high   = MathF.Max(corner1.Z, corner2.Z);

        Span<(float X, float Y)> round = [(corner1.X, corner1.Y), (corner2.X, corner1.Y), (corner2.X, corner2.Y), (corner1.X, corner2.Y)];
        var corners = new Vector[8];

        for (var i = 0; i < 4; i++)
        {
            corners[i]     = Turn(new Vector(round[i].X, round[i].Y, low), center, yaw);
            corners[i + 4] = Turn(new Vector(round[i].X, round[i].Y, high), center, yaw);
        }

        return corners;
    }

    // Whether the point is in the box turned about its centre.
    public static bool Contains(Vector corner1, Vector corner2, float yaw, Vector point)
    {
        var center = (corner1 + corner2) / 2f;
        var local  = Turn(point, center, -yaw);

        return local.X >= MathF.Min(corner1.X, corner2.X) && local.X <= MathF.Max(corner1.X, corner2.X)
            && local.Y >= MathF.Min(corner1.Y, corner2.Y) && local.Y <= MathF.Max(corner1.Y, corner2.Y)
            && local.Z >= MathF.Min(corner1.Z, corner2.Z) && local.Z <= MathF.Max(corner1.Z, corner2.Z);
    }

    // !zone's verbs for a zone added in game; turn is rotate's other name.
    public static string? EditVerb(string arg)
        => arg.ToLowerInvariant() switch
        {
            "edit"             => "edit",
            "move"             => "move",
            "rotate" or "turn" => "rotate",
            "done"             => "done",
            _                  => null,
        };

    // "x y z" in units; each a finite number.
    public static bool TryParseMove(IReadOnlyList<string> args, out Vector delta)
    {
        delta = new Vector();

        if (args.Count != 3
            || !TryParseNumber(args[0], out var x)
            || !TryParseNumber(args[1], out var y)
            || !TryParseNumber(args[2], out var z))
        {
            return false;
        }

        delta = new Vector(x, y, z);

        return true;
    }

    // Degrees, anticlockwise from above.
    public static bool TryParseTurn(IReadOnlyList<string> args, out float degrees)
    {
        degrees = 0f;

        return args.Count == 1 && TryParseNumber(args[0], out degrees);
    }

    private static bool TryParseNumber(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    public static string FormatPoint(Vector point)
        => string.Create(CultureInfo.InvariantCulture, $"{point.X:0.#} {point.Y:0.#} {point.Z:0.#}");

    public static string FormatYaw(float yaw)
        => yaw.ToString("0.#", CultureInfo.InvariantCulture);

    // Stage 1 is the start zone, so stages count from 2.
    public static int FirstNumber(EZoneType type)
        => type switch
        {
            EZoneType.Stage      => 2,
            EZoneType.Checkpoint => 1,
            _                    => 0,
        };

    // 到终点还要补一条末段, 一条成绩最多提交 MAX_STAGE - 1 条检查点
    public static int LastNumber(EZoneType type)
        => type == EZoneType.Checkpoint ? MaxNumber - 1 : MaxNumber;

    public static bool IsNumbered(EZoneType type)
        => type is EZoneType.Stage or EZoneType.Checkpoint;

    public static bool IsValid(int track, EZoneType type, int number)
        => track is >= 0 and < TimerConstants.MAX_TRACK
           && type is > EZoneType.Invalid and < EZoneType.Max
           && (IsNumbered(type) ? number >= FirstNumber(type) && number <= LastNumber(type) : number == 0);

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

        return Math.Min(next, LastNumber(type));
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
