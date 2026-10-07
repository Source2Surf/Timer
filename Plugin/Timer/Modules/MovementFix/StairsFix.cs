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
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's stairs fix, surf only, on the ground too: CS2's own step up stops a fast player at the next step's face. A move
// stopped by a step's face goes on from the top of the step, step after step, with the speed they had.
internal unsafe partial class MovementFixModule
{
    private const int MaxStairsBumps = 8;

    private static void ApplyStairsFix(nint      service,
                                       nint      pawn,
                                       int       slot,
                                       MoveData* mv,
                                       Vector    start,
                                       Vector    velocity,
                                       bool      onGround)
    {
        if (!_stairsEnabled
            || !_canQueryTriggers
            || _globals == nint.Zero
            || _isFakeClient[slot]
            || (mv->Velocity - velocity).LengthSqr() < 1e-4f // TryPlayerMove ran into nothing
            || (!onGround && velocity.Z > 0.0f)
            || GetMoveType(pawn) != MoveType.Walk
            || IsInWater(pawn)
            || _mapInfo.GetCurrentGameMode() != EGameMode.Surf)
        {
            return;
        }

        var frameTime = *(float*) (_globals + CGlobalVars_frametime_offset);
        var move      = velocity * frameTime;
        var length    = new Vector(move.X, move.Y, 0.0f).Length();

        if (length < 1.0f)
        {
            return;
        }

        var direction = new Vector(move.X / length, move.Y / length, 0.0f);
        var filter    = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        var ray   = CreatePlayerHull(service);
        var trace = stackalloc CGameTrace[1];

        // StepMove's move from a step up: left alone.
        if (onGround && !FindGround(start, &ray, filter, trace, out _))
        {
            return;
        }

        var position = start;
        var end      = start + move;
        var steps    = 0;

        for (var bump = 0; bump < MaxStairsBumps; bump++)
        {
            if (bump > 0)
            {
                var remaining = length - (position - start).Dot(direction);

                if (remaining <= 0.0f)
                {
                    break;
                }

                end = position + direction * remaining;
            }

            TracePlayerBBox(&position, &end, &ray, filter, trace);

            if (trace->StartInSolid)
            {
                break;
            }

            if (trace->Fraction >= 1.0f)
            {
                position = end;

                break;
            }

            position = trace->EndPosition;

            var normal = trace->PlaneNormal;

            // Landed on a step's top: the rest of the move goes on along it.
            if (bump == 0 && normal.Z >= 1.0f - 1e-4f)
            {
                continue;
            }

            if (MathF.Abs(normal.Z) > 1e-4f
                || !TryStepUp(&ray, filter, trace, pawn, position, direction, out var top))
            {
                break;
            }

            position = top;
            steps++;
        }

        var further = (position - mv->AbsOrigin).Dot(direction);

        if (steps == 0 || further <= 0.03125f)
        {
            return;
        }

        mv->AbsOrigin = position;
        mv->Velocity  = velocity;
    }

    // StepMove from where they ran into a face: walkable ground within a step below, up a step, 1 unit over, and back down
    // onto walkable ground higher up.
    private static bool TryStepUp(TraceShapeRay* ray,
                                  CTraceFilter*  filter,
                                  CGameTrace*    trace,
                                  nint           pawn,
                                  Vector         contact,
                                  Vector         direction,
                                  out Vector     top)
    {
        top = default;

        var below = contact;
        below.Z -= _stepSize;

        TracePlayerBBox(&contact, &below, ray, filter, trace);

        if (!trace->DidHit() || trace->PlaneNormal.Z < _standableNormal)
        {
            return false;
        }

        var bottom = trace->EndPosition;
        var up     = bottom;
        up.Z += _stepSize;

        TracePlayerBBox(&bottom, &up, ray, filter, trace);

        if (trace->DidHit())
        {
            up = trace->EndPosition;
        }

        var over = up + direction;

        TracePlayerBBox(&up, &over, ray, filter, trace);

        if (trace->DidHit())
        {
            return false;
        }

        var down = over;
        down.Z -= _stepSize;

        TracePlayerBBox(&over, &down, ray, filter, trace);

        if (!trace->DidHit() || trace->PlaneNormal.Z < _standableNormal || trace->EndPosition.Z <= bottom.Z + 0.03125f)
        {
            return false;
        }

        top = trace->EndPosition;

        // Dropping onto a ledge with a fail teleport in front, likely, not stairs.
        return contact.Z - bottom.Z <= 1.0f || !WouldTrigger(ray, bottom, contact, pawn);
    }

    // A trigger they'd touch standing there; one they're in already changes nothing.
    private static bool WouldTrigger(TraceShapeRay* ray, Vector point, Vector current, nint pawn)
    {
        var hits     = stackalloc nint[MaxTriggerHits];
        var count    = CollectTriggers(ray, point, hits);
        var touching = stackalloc nint[MaxTriggerHits];
        var touched  = CollectTriggers(ray, current, touching);

        for (var i = 0; i < count; i++)
        {
            if (!Contains(touching, touched, hits[i]) && PassesTriggerFilters(hits[i], pawn))
            {
                return true;
            }
        }

        return false;
    }
}
