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
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's pre-tick fixes, run before each airborne TryPlayerMove. When the move would hit walkable ground in a way
// that costs the player (an uphill slope's clip, or sliding off a block's edge without landing), rewind it to end just
// above the point of contact, so CategorizePosition lands them there instead.
internal unsafe partial class MovementFixModule
{
    // CategorizePosition doesn't land a player moving up faster than this.
    private const float NonJumpVelocity = 140.0f;

    // How far below the player CategorizePosition looks for ground.
    private const float LandHeight = 2.0f;

    private static void ApplyPreMoveFixes(nint service, nint pawn, int slot, MoveData* mv)
    {
        if (!(_uphillEnabled || _edgebugEnabled)
            || !_canTrace
            || _globals == nint.Zero
            || _isFakeClient[slot]
            || GetMoveType(pawn) != MoveType.Walk
            || IsInWater(pawn))
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

        // frametime is the length of this movement step.
        var frameTime = *(float*) (_globals + CGlobalVars_frametime_offset);
        var move      = velocity * frameTime;
        var origin    = mv->AbsOrigin;
        var end       = origin + move;
        var ray       = CreatePlayerHull(service);
        var trace     = stackalloc CGameTrace[1];

        TracePlayerBBox(&origin, &end, &ray, filter, trace);

        if (trace->StartInSolid || trace->Fraction >= 1.0f || trace->PlaneNormal.Z < _standableNormal)
        {
            return;
        }

        var collisionPoint = trace->EndPosition;
        var normal         = trace->PlaneNormal;
        var timeLeft       = frameTime * (1.0f - trace->Fraction);

        // Landing on the slope rules out an edgebug too.
        if (_uphillEnabled && IsCostlyUphillCollision(velocity, normal))
        {
            PreventCollision(mv, collisionPoint, move, &ray, filter, trace);

            return;
        }

        if (_edgebugEnabled && WouldEdgebug(velocity, normal, collisionPoint, timeLeft, &ray, filter, trace))
        {
            PreventCollision(mv, collisionPoint, move, &ray, filter, trace);
        }
    }

    // A slope the player moves into, unless the clip would speed them up.
    private static bool IsCostlyUphillCollision(Vector velocity, Vector normal)
    {
        if (normal.Z >= 1.0f || normal.X * velocity.X + normal.Y * velocity.Y >= 0.0f)
        {
            return false;
        }

        return !_slopefixEnabled || ClipVelocity(velocity, normal).Length2DSqr() <= velocity.Length2DSqr();
    }

    // Whether the move, sliding along what it hit, would end with no ground to land on.
    private static bool WouldEdgebug(Vector        velocity,
                                     Vector        normal,
                                     Vector        collisionPoint,
                                     float         timeLeft,
                                     TraceShapeRay* ray,
                                     CTraceFilter*  filter,
                                     CGameTrace*    trace)
    {
        var clipped = ClipVelocity(velocity, normal);

        if (clipped.Z > NonJumpVelocity)
        {
            return false;
        }

        return !FindGround(collisionPoint + clipped * timeLeft, ray, filter, trace, out _);
    }

    // rngfix's PreventCollision. The rewound start sits behind the player, so the way from it has to be clear.
    private static void PreventCollision(MoveData*      mv,
                                         Vector         collisionPoint,
                                         Vector         move,
                                         TraceShapeRay* ray,
                                         CTraceFilter*  filter,
                                         CGameTrace*    trace)
    {
        var rewound = collisionPoint - move;
        rewound.Z += 0.1f;

        var landing = collisionPoint;
        landing.Z += 0.1f;

        TracePlayerBBox(&rewound, &landing, ray, filter, trace);

        if (trace->StartInSolid || trace->Fraction < 1.0f)
        {
            return;
        }

        mv->AbsOrigin = rewound;
    }

    // CS2's ClipVelocity with the overbounce of 1 TryPlayerMove uses on walkable planes.
    private static Vector ClipVelocity(Vector velocity, Vector normal)
        => velocity + normal * (MathF.Max(-velocity.Dot(normal), 0.0f) + 0.03125f);

    // Standable ground within LandHeight, with TracePlayerBBoxForGround's quadrant fallback.
    private static bool FindGround(Vector         point,
                                   TraceShapeRay* ray,
                                   CTraceFilter*  filter,
                                   CGameTrace*    trace,
                                   out Vector     normal)
    {
        var below = point;
        below.Z -= LandHeight;

        TracePlayerBBox(&point, &below, ray, filter, trace);

        normal = trace->PlaneNormal;

        if (!trace->DidHit())
        {
            return false;
        }

        if (normal.Z >= _standableNormal)
        {
            return true;
        }

        var mins = ray->Hull.Mins;
        var maxs = ray->Hull.Maxs;

        return FindGroundInQuadrant(point, below, new (mins.X, mins.Y, mins.Z), new (0, 0, maxs.Z), filter, trace, out normal)
               || FindGroundInQuadrant(point, below, new (0, 0, mins.Z), new (maxs.X, maxs.Y, maxs.Z), filter, trace, out normal)
               || FindGroundInQuadrant(point, below, new (mins.X, 0, mins.Z), new (0, maxs.Y, maxs.Z), filter, trace, out normal)
               || FindGroundInQuadrant(point, below, new (0, mins.Y, mins.Z), new (maxs.X, 0, maxs.Z), filter, trace, out normal);
    }

    private static bool FindGroundInQuadrant(Vector        point,
                                             Vector        below,
                                             Vector        mins,
                                             Vector        maxs,
                                             CTraceFilter* filter,
                                             CGameTrace*   trace,
                                             out Vector    normal)
    {
        var quadrant = new TraceShapeRay(new TraceShapeHull { Mins = mins, Maxs = maxs });

        TracePlayerBBox(&point, &below, &quadrant, filter, trace);

        normal = trace->PlaneNormal;

        return trace->DidHit() && normal.Z >= _standableNormal;
    }
}
