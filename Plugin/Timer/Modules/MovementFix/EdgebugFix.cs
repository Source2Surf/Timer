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
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// Edgebug fix, after rngfix's (jason-e/rngfix, the edge half of RunPreTickChecks).
//
// A player who comes down onto the edge of a block can hit its top mid-step, slide off it and end the step past the
// edge with nothing under them. TryPlayerMove has zeroed their fall but CategorizePosition finds no ground, so they
// never land, which in bhop means no jump and a fall. Whether that happens depends on where in the step they hit.
// Before such a step, this rewinds it so it ends just above the point of contact instead, and CategorizePosition
// lands the player there.
internal unsafe partial class MovementFixModule
{
    // CategorizePosition won't put a player moving up faster than this on the ground.
    private const float NonJumpVelocity = 140.0f;

    // How far below the player CategorizePosition looks for ground.
    private const float LandHeight = 2.0f;

    private static void ApplyEdgebugFix(nint service, nint pawn, int slot, MoveData* mv)
    {
        if (!_edgebugEnabled || !_canTrace || _globals == nint.Zero || _isFakeClient[slot]
            || GetMoveType(pawn) != MoveType.Walk || IsInWater(pawn))
        {
            return;
        }

        var velocity = mv->Velocity;

        if (velocity.Z > NonJumpVelocity)
        {
            return;
        }

        var filter = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        // TryPlayerMove moves the player by velocity * frametime, and frametime is the length of this step.
        var frameTime = *(float*) (_globals + CGlobalVars_frametime_offset);
        var move      = velocity * frameTime;
        var origin    = mv->AbsOrigin;
        var end       = origin + move;
        var ray       = CreatePlayerHull(service);
        var trace     = stackalloc CGameTrace[1];

        TracePlayerBBox(&origin, &end, &ray, filter, trace);

        // Only a walkable surface can be landed on.
        if (trace->StartInSolid || trace->Fraction >= 1.0f || trace->PlaneNormal.Z < _standableNormal)
        {
            return;
        }

        var collisionPoint = trace->EndPosition;
        var normal         = trace->PlaneNormal;
        var fractionLeft   = 1.0f - trace->Fraction;

        // Roughly where the step ends after sliding along what it hit, assuming no second collision. On flat ground
        // the clip just drops the vertical speed.
        var clipped = velocity - normal * velocity.Dot(normal);

        // Sliding up a slope this fast, the player can't land wherever the step ends.
        if (clipped.Z > NonJumpVelocity)
        {
            return;
        }

        var stepEnd = collisionPoint + clipped * (frameTime * fractionLeft);

        if (HasGroundBelow(stepEnd, &ray, filter, trace))
        {
            return;
        }

        // Rewind part of the step so it ends a hair above the point of contact. It loses a fraction of a step of
        // travel, like a mid-step landing would.
        var rewound = collisionPoint - move;
        rewound.Z += 0.1f;

        var landing = collisionPoint;
        landing.Z += 0.1f;

        // The rewound start sits behind the player, so make sure it and the way back to the edge are clear.
        TracePlayerBBox(&rewound, &landing, &ray, filter, trace);

        if (trace->StartInSolid || trace->Fraction < 1.0f)
        {
            return;
        }

        mv->AbsOrigin = rewound;
    }

    // What CategorizePosition would find under the player: standable ground within LandHeight, falling back to the
    // four quadrants of the hull like CGameMovement::TracePlayerBBoxForGround.
    private static bool HasGroundBelow(Vector point, TraceShapeRay* ray, CTraceFilter* filter, CGameTrace* trace)
    {
        var below = point;
        below.Z -= LandHeight;

        TracePlayerBBox(&point, &below, ray, filter, trace);

        if (!trace->DidHit())
        {
            return false;
        }

        if (trace->PlaneNormal.Z >= _standableNormal)
        {
            return true;
        }

        var mins = ray->Hull.Mins;
        var maxs = ray->Hull.Maxs;

        return HasGroundBelowQuadrant(point, below, new (mins.X, mins.Y, mins.Z), new (0, 0, maxs.Z), filter, trace)
               || HasGroundBelowQuadrant(point, below, new (0, 0, mins.Z), new (maxs.X, maxs.Y, maxs.Z), filter, trace)
               || HasGroundBelowQuadrant(point, below, new (mins.X, 0, mins.Z), new (0, maxs.Y, maxs.Z), filter, trace)
               || HasGroundBelowQuadrant(point, below, new (0, mins.Y, mins.Z), new (maxs.X, 0, maxs.Z), filter, trace);
    }

    private static bool HasGroundBelowQuadrant(Vector       point,
                                               Vector       below,
                                               Vector       mins,
                                               Vector       maxs,
                                               CTraceFilter* filter,
                                               CGameTrace*   trace)
    {
        var quadrant = new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs });

        TracePlayerBBox(&point, &below, &quadrant, filter, trace);

        return trace->DidHit() && trace->PlaneNormal.Z >= _standableNormal;
    }
}
