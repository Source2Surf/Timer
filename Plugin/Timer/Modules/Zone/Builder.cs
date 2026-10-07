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
using System.Runtime.CompilerServices;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Zone;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class ZoneModule
{
    private HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      arg1,
                                                                   HookReturnValue<EmptyHookReturn> arg2)
    {
        var client = arg1.Client;

        if (client.IsFakeClient)
        {
            return new ();
        }

        var pawn = arg1.Pawn;

        if (!pawn.IsAlive || _buildZoneInfo[client.Slot] is not { } buildInfo)
        {
            return new ();
        }

        var eyepos = pawn.GetEyePosition();
        eyepos.Z -= 2f;

        pawn.GetEyeAngles().AnglesToVectorSource2(out var direction, out _, out _);

        var end = eyepos + (direction * 1024.0f);

        var attribute = RnQueryShapeAttr.Bullets();
        attribute.HitTrigger = false;
        attribute.SetEntityToIgnore(pawn, 0);

        var result = _bridge.PhysicsQueryManager.TraceLineNoPlayers(eyepos, end, attribute);

        const int snapGrid = 2;

        var snapped = SnapToGrid(result.EndPosition + new Vector(0, 0, 3), snapGrid);

        if (buildInfo.Lines.Due())
        {
            DrawGuides(buildInfo, eyepos, snapped, snapGrid);
        }

        if ((arg1.KeyButtons & UserCommandButtons.Use) == 0 || (arg1.ChangedButtons & UserCommandButtons.Use) == 0)
        {
            return new ();
        }

        if (buildInfo.Step == 0)
        {
            buildInfo.Points[0] = snapped;
            buildInfo.Step++;
            EditVersion++;
        }
        else if (buildInfo.Step == 1)
        {
            buildInfo.Points[1] = snapped + new Vector(0, 0, 128);

            AddZone(new ()
            {
                Track    = buildInfo.Track,
                ZoneType = buildInfo.Zone,
                Data     = buildInfo.Number,
                Prebuilt = false,
                Corner1  = buildInfo.Points[0],
                Corner2  = buildInfo.Points[1],
            });

            buildInfo.Lines.Clear();

            _buildZoneInfo[client.Slot] = null;
            EditVersion++;

            SaveCustomZones();
        }

        return new ();
    }

    // The aim line, a cross on the snapped point and, once the first corner is down, the box so far.
    private static void DrawGuides(BuildZoneInfo buildInfo, Vector eyepos, Vector snapped, int snapGrid)
    {
        var lines = buildInfo.Lines;
        var half  = snapGrid / 2f;

        lines.Draw(BuildLines.Direction, eyepos, snapped, BuildLines.White);
        lines.Draw(BuildLines.Snap, snapped + new Vector(half, 0, 0), snapped - new Vector(half, 0, 0), BuildLines.Red);
        lines.Draw(BuildLines.Snap + 1, snapped - new Vector(0, half, 0), snapped + new Vector(0, half, 0), BuildLines.Red);

        if (buildInfo.Step == 1)
        {
            lines.DrawBox(buildInfo.Points[0], snapped + new Vector(0, 0, 128));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector SnapToGrid(in Vector pos, int grid)
    {
        if (grid <= 1)
        {
            return pos;
        }

        var gridF = (float) grid;

        var snappedX = (float) Math.Round(pos.X / gridF, MidpointRounding.AwayFromZero) * gridF;
        var snappedY = (float) Math.Round(pos.Y / gridF, MidpointRounding.AwayFromZero) * gridF;

        return new (snappedX, snappedY, pos.Z);
    }
}
