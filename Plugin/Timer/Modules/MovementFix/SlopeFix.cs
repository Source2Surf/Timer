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

// rngfix's downhill incline fix, run where the game lands the player. Landing zeroes vertical velocity, so landing on a
// downhill slope without colliding with it first loses the speed the slope would have given.
internal unsafe partial class MovementFixModule
{
    // moveVelocity is what this step's TryPlayerMove moved with, rngfix's pre-collision velocity.
    private static void ApplySlopeFix(nint service, nint pawn, int slot, MoveData* mv, Vector moveVelocity)
    {
        if (!_slopefixEnabled || !_canTrace || _isFakeClient[slot] || GetMoveType(pawn) != MoveType.Walk
            || IsInWater(pawn))
        {
            return;
        }

        if (moveVelocity.Z > 0.0f)
        {
            return;
        }

        // CS2's TryPlayerMove stops falling players this slow on standable planes.
        if (moveVelocity.Length2DSqr() < 1.0f)
        {
            return;
        }

        var filter = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        var ray   = CreatePlayerHull(service);
        var trace = stackalloc CGameTrace[1];

        if (!FindGround(mv->AbsOrigin, &ray, filter, trace, out var normal) || normal.Z >= 1.0f)
        {
            return;
        }

        var clipped = ClipVelocity(moveVelocity, normal);

        // Never slower, unlike rngfix's downhill branch.
        if (clipped.Length2DSqr() < moveVelocity.Length2DSqr())
        {
            return;
        }

        // The game adds the base velocity back on its own.
        var baseVelocity = *(Vector*) (pawn + CBaseEntity_m_vecBaseVelocity_offset);

        mv->Velocity = new (clipped.X - baseVelocity.X, clipped.Y - baseVelocity.Y, mv->Velocity.Z);
    }
}
