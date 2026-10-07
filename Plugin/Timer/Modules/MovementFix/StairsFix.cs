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

using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's stairs fix, surf only. Only a player on the ground steps up stairs, so one sliding down them in the air stops
// at the next step's face. Put them on top of it instead, with the speed they had.
internal unsafe partial class MovementFixModule
{
    private static void ApplyStairsFix(nint service, nint pawn, int slot, MoveData* mv, Vector origin, Vector velocity)
    {
        if (!_stairsEnabled
            || !_canQueryTriggers
            || _globals == nint.Zero
            || _isFakeClient[slot]
            || velocity.Z > 0.0f // the player could never slide up the step
            || (mv->Velocity - velocity).LengthSqr() < 1e-4f // TryPlayerMove ran into nothing
            || GetMoveType(pawn) != MoveType.Walk
            || IsInWater(pawn)
            || _mapInfo.GetCurrentGameMode() != EGameMode.Surf)
        {
            return;
        }

        var frameTime = *(float*) (_globals + CGlobalVars_frametime_offset);
        var direction = new Vector(velocity.X, velocity.Y, 0.0f);
        var speed     = direction.Length();

        if (speed * frameTime < 1.0f)
        {
            return;
        }

        direction /= speed;

        var filter = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        var ray   = CreatePlayerHull(service);
        var trace = stackalloc CGameTrace[1];
        var end   = origin + velocity * frameTime;

        TracePlayerBBox(&origin, &end, &ray, filter, trace);

        // A step's face is vertical.
        if (trace->StartInSolid || trace->Fraction >= 1.0f || trace->PlaneNormal.Z != 0.0f)
        {
            return;
        }

        // Walkable ground within a step below where they ran into it.
        var collision = trace->EndPosition;
        var below     = collision;
        below.Z -= _stepSize;

        TracePlayerBBox(&collision, &below, &ray, filter, trace);

        if (!trace->DidHit() || trace->PlaneNormal.Z < _standableNormal)
        {
            return;
        }

        var bottom = trace->EndPosition;

        // Likely a ledge with a fail teleport in front, not stairs.
        if (WouldTrigger(&ray, bottom, pawn))
        {
            return;
        }

        // StepMove: up a step, 1 unit over, and back down onto walkable ground.
        var up = bottom;
        up.Z += _stepSize;

        TracePlayerBBox(&bottom, &up, &ray, filter, trace);

        if (trace->DidHit())
        {
            up = trace->EndPosition;
        }

        var over = up + direction;

        TracePlayerBBox(&up, &over, &ray, filter, trace);

        if (trace->DidHit())
        {
            return;
        }

        var down = over;
        down.Z -= _stepSize;

        TracePlayerBBox(&over, &down, &ray, filter, trace);

        if (!trace->DidHit() || trace->PlaneNormal.Z < _standableNormal)
        {
            return;
        }

        mv->AbsOrigin = trace->EndPosition;
        mv->Velocity  = velocity;
    }

    private static bool WouldTrigger(TraceShapeRay* ray, Vector point, nint pawn)
    {
        var hits  = stackalloc nint[MaxTriggerHits];
        var count = CollectTriggers(ray, point, hits);

        for (var i = 0; i < count; i++)
        {
            if (PassesTriggerFilters(hits[i], pawn))
            {
                return true;
            }
        }

        return false;
    }
}
