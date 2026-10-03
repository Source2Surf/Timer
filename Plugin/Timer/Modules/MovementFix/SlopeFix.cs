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
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Source2Surf.Timer.Extensions;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's downhill incline fix, run where the game lands the player. Landing zeroes vertical velocity, so landing on a
// downhill slope without colliding with it first loses the speed the slope would have given.
internal unsafe partial class MovementFixModule
{
    // ReSharper disable InconsistentNaming

    private static delegate* unmanaged<nint, MoveData*, bool, void> CCSPlayer_MovementServices_CategorizePosition;

    private static int CPlayerPawnComponent_m_pChainEntity_offset;
    private static int CBaseEntity_m_hGroundEntity_offset;
    private static int CCSPlayer_MovementServices_m_bDucked_offset;

    // ReSharper restore InconsistentNaming

    private void InstallSlopeFix()
    {
        var schema = _bridge.SchemaManager;

        CPlayerPawnComponent_m_pChainEntity_offset  = schema.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBaseEntity_m_hGroundEntity_offset          = schema.GetNetVarOffset("CBaseEntity", "m_hGroundEntity");
        CCSPlayer_MovementServices_m_bDucked_offset = schema.GetNetVarOffset("CCSPlayer_MovementServices", "m_bDucked");

        // A broken lookup only disables slopefix.
        var address = FindCategorizePosition();

        if (address == nint.Zero)
        {
            _logger.LogWarning("Failed to find CCSPlayer_MovementServices::CategorizePosition, slopefix is disabled");

            return;
        }

        if (!_inlineHookManager.AddHook(address,
                                        (nint) (delegate* unmanaged<nint, MoveData*, bool, void>)
                                        (&hk_CCSPlayer_MovementServices_CategorizePosition),
                                        out var trampoline))
        {
            _logger.LogWarning("Failed to hook CCSPlayer_MovementServices::CategorizePosition, slopefix is disabled");

            return;
        }

        CCSPlayer_MovementServices_CategorizePosition = (delegate* unmanaged<nint, MoveData*, bool, void>) trampoline;
    }

    // The scan survives recompiles that break signatures, and the signature backs it up. When they disagree, one of them
    // found the wrong function, so neither is used.
    private nint FindCategorizePosition()
    {
        var scanned = ScanCategorizePosition();

        _bridge.ModSharp.GetGameData().GetAddress("CCSPlayer_MovementServices::CategorizePosition", out var signature);

        if (scanned != nint.Zero && signature != nint.Zero && scanned != signature)
        {
            _logger.LogError("CCSPlayer_MovementServices::CategorizePosition: heuristic scan found 0x{Scanned:X} but the signature found 0x{Signature:X}",
                             scanned,
                             signature);

            return nint.Zero;
        }

        return scanned != nint.Zero ? scanned : signature;
    }

    // PlayerMove's post-move step calls CategorizePosition right before passing "PlayerMove_PostMove" to the next call,
    // so it is the last direct call before that string is loaded.
    private nint ScanCategorizePosition()
    {
        var server = _bridge.Modules.Server;

        var postMoveString = server.FindStringExact("PlayerMove_PostMove");

        if (postMoveString == nint.Zero
            || server.GetReferencesFromPointer(postMoveString) is not [var reference]
            || !server.GetFunctionRange(reference, out var start, out _)
            || reference < start)
        {
            return nint.Zero;
        }

        // Read through the referencing instruction too.
        var reader  = new UnsafeCodeReader((byte*) start, (uint) (reference - start + 16));
        var decoder = Decoder.Create(64, reader, (ulong) start, DecoderOptions.AMD);

        var lastCall = nint.Zero;

        while (reader.CanReadByte)
        {
            var instr = decoder.Decode();

            if (instr.IsInvalid)
            {
                continue;
            }

            if (instr.IsIPRelativeMemoryOperand && (nint) instr.IPRelativeMemoryAddress == postMoveString)
            {
                return lastCall != nint.Zero
                       && server.GetFunctionRange(lastCall, out var calleeStart, out _)
                       && calleeStart == lastCall
                    ? lastCall
                    : nint.Zero;
            }

            if (instr.Code == Code.Call_rel32_64)
            {
                var target = (nint) instr.NearBranchTarget;

                // Skip `ret` stubs.
                if (*(byte*) target != 0xC3)
                {
                    lastCall = target;
                }
            }
        }

        return nint.Zero;
    }

    private static bool IsOnGround(nint pawn)
        => *(uint*) (pawn + CBaseEntity_m_hGroundEntity_offset) != uint.MaxValue;

    [UnmanagedCallersOnly]
    private static void hk_CCSPlayer_MovementServices_CategorizePosition(nint service, MoveData* mv, bool stayOnGround)
    {
        var pawn = *(nint*) (service + CPlayerPawnComponent_m_pChainEntity_offset);

        // stayOnGround is the pawn's FL_ONGROUND.
        if (stayOnGround || pawn == nint.Zero || IsOnGround(pawn))
        {
            CCSPlayer_MovementServices_CategorizePosition(service, mv, stayOnGround);

            return;
        }

        var landingVelocity = mv->Velocity;

        CCSPlayer_MovementServices_CategorizePosition(service, mv, stayOnGround);

        if (!IsOnGround(pawn) || _instance is not { } module)
        {
            return;
        }

        // An exception escaping an unmanaged callback takes the server down with it.
        try
        {
            module.OnLanded(service, pawn, mv, landingVelocity);
        }
        catch (Exception e)
        {
            module._logger.LogError(e, "Error while applying slopefix");
        }
    }

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
