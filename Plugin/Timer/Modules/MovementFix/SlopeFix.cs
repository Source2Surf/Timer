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
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's downhill incline fix, run where the game lands the player. Landing zeroes vertical velocity, so landing on a
// downhill slope without colliding with it first loses the speed the slope would have given.
internal unsafe partial class MovementFixModule
{
    private void OnLanded(nint service, nint pawnPtr, MoveData* mv, Vector landingVelocity)
    {
        if (!timer_slopefix.GetBool())
        {
            return;
        }

        // CS2's TryPlayerMove stops falling players this slow on standable planes.
        if (landingVelocity.Length2DSqr() < 1.0f)
        {
            return;
        }

        var pawn = _bridge.EntityManager.MakeEntityFromPointer<IPlayerPawn>(pawnPtr);

        if (pawn.ActualMoveType != MoveType.Walk || pawn.GetController() is not { IsFakeClient: false })
        {
            return;
        }

        var hull = new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, *(bool*) (service + CCSPlayer_MovementServices_m_bDucked_offset) ? 54 : 72),
        };

        var origin = mv->AbsOrigin;
        var ground = origin;
        ground.Z -= 2.0f;

        var attribute = RnQueryShapeAttr.PlayerMovement(pawn.GetCollisionProperty()!.CollisionAttribute.InteractsWith);
        attribute.SetEntityToIgnore(pawn, 0);

        var trace = _bridge.PhysicsQueryManager.TraceShapePlayerMovement(new (hull), origin, ground, attribute);

        if (trace.StartInSolid || trace.Fraction >= 1.0f)
        {
            return;
        }

        var normal = trace.PlaneNormal;

        if (normal.Z >= 1.0f || normal.Z < sv_standable_normal.GetFloat())
        {
            return;
        }

        // ClipVelocity, overbounce 1.
        var clipped = landingVelocity - normal * landingVelocity.Dot(normal);
        var adjust  = clipped.Dot(normal);

        if (adjust < 0.0f)
        {
            clipped -= normal * adjust;
        }

        // Never slower.
        if (clipped.Length2DSqr() < landingVelocity.Length2DSqr())
        {
            return;
        }

        mv->Velocity = new (clipped.X, clipped.Y, mv->Velocity.Z);
    }
}
