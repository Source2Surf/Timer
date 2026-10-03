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
using Sharp.Shared;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// The native detours the movement fixes share. A hook that can't be found only disables the fixes that need it.
internal unsafe partial class MovementFixModule
{
    // ReSharper disable InconsistentNaming

    private static delegate* unmanaged<nint, MoveData*, bool, void>             CCSPlayer_MovementServices_CategorizePosition;
    private static delegate* unmanaged<nint, MoveData*, nint, nint, nint, void> CCSPlayer_MovementServices_TryPlayerMove;
    private static delegate* unmanaged<nint, nint, nint>                        CTriggerTeleport_Teleport;

    private static int CPlayerPawnComponent_m_pChainEntity_offset;
    private static int CBaseEntity_m_hGroundEntity_offset;
    private static int CBasePlayerPawn_m_hController_offset;
    private static int CCSPlayer_MovementServices_m_bDucked_offset;

    // ReSharper restore InconsistentNaming

    private void InstallHooks()
    {
        var schema = _bridge.SchemaManager;

        CPlayerPawnComponent_m_pChainEntity_offset  = schema.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBaseEntity_m_hGroundEntity_offset          = schema.GetNetVarOffset("CBaseEntity", "m_hGroundEntity");
        CBasePlayerPawn_m_hController_offset        = schema.GetNetVarOffset("CBasePlayerPawn", "m_hController");
        CCSPlayer_MovementServices_m_bDucked_offset = schema.GetNetVarOffset("CCSPlayer_MovementServices", "m_bDucked");

        if (Hook("CCSPlayer_MovementServices::CategorizePosition",
                 FindCategorizePosition(),
                 (nint) (delegate* unmanaged<nint, MoveData*, bool, void>) (&hk_CCSPlayer_MovementServices_CategorizePosition),
                 out var trampoline))
        {
            CCSPlayer_MovementServices_CategorizePosition = (delegate* unmanaged<nint, MoveData*, bool, void>) trampoline;
        }

        if (Hook("CCSPlayer_MovementServices::TryPlayerMove",
                 FindFunction("CCSPlayer_MovementServices::TryPlayerMove"),
                 (nint) (delegate* unmanaged<nint, MoveData*, nint, nint, nint, void>) (&hk_CCSPlayer_MovementServices_TryPlayerMove),
                 out trampoline))
        {
            CCSPlayer_MovementServices_TryPlayerMove = (delegate* unmanaged<nint, MoveData*, nint, nint, nint, void>) trampoline;
        }

        if (Hook("CTriggerTeleport teleport",
                 FindTriggerTeleportTeleport(),
                 (nint) (delegate* unmanaged<nint, nint, nint>) (&hk_CTriggerTeleport_Teleport),
                 out trampoline))
        {
            CTriggerTeleport_Teleport = (delegate* unmanaged<nint, nint, nint>) trampoline;
        }
    }

    private bool Hook(string name, nint address, nint detour, out nint trampoline)
    {
        trampoline = nint.Zero;

        if (address == nint.Zero)
        {
            _logger.LogWarning("Failed to find {Name}, the movement fixes that need it are disabled", name);

            return false;
        }

        if (!_inlineHookManager.AddHook(address, detour, out trampoline))
        {
            _logger.LogWarning("Failed to hook {Name}, the movement fixes that need it are disabled", name);

            return false;
        }

        return true;
    }

    private static bool IsFunctionStart(ILibraryModule module, nint address)
        => address != nint.Zero && module.GetFunctionRange(address, out var start, out _) && start == address;

    // Detouring a signature hit in the middle of a function would take the server down.
    private nint FindFunction(string gamedataKey)
        => _bridge.ModSharp.GetGameData().GetAddress(gamedataKey, out var address)
           && IsFunctionStart(_bridge.Modules.Server, address)
            ? address
            : nint.Zero;

    // The scan survives recompiles that break signatures, and the signature backs it up. When they disagree, one of them
    // found the wrong function, so neither is used.
    private nint FindCategorizePosition()
    {
        var scanned   = ScanCategorizePosition();
        var signature = FindFunction("CCSPlayer_MovementServices::CategorizePosition");

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
                return IsFunctionStart(server, lastCall) ? lastCall : nint.Zero;
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

    // CBaseTrigger::StartTouch calls this virtual with the toucher, the only one past CBaseToggle's that CTriggerTeleport
    // overrides (index 273 of 279 on Windows, 2026-10).
    private nint FindTriggerTeleportTeleport()
    {
        var server   = _bridge.Modules.Server;
        var toggle   = server.GetVirtualFunctions("CBaseToggle");
        var trigger  = server.GetVirtualFunctions("CBaseTrigger");
        var teleport = server.GetVirtualFunctions("CTriggerTeleport");

        if (toggle.Length == 0 || trigger.Length <= toggle.Length || teleport.Length != trigger.Length)
        {
            return nint.Zero;
        }

        var found = nint.Zero;

        for (var i = toggle.Length; i < trigger.Length; i++)
        {
            if (teleport[i] == trigger[i])
            {
                continue;
            }

            if (found != nint.Zero)
            {
                return nint.Zero;
            }

            found = teleport[i];
        }

        return found;
    }

    private static bool IsOnGround(nint pawn)
        => *(uint*) (pawn + CBaseEntity_m_hGroundEntity_offset) != uint.MaxValue;

    // The controller's entity index is its slot + 1.
    private static int GetPlayerSlot(nint pawn)
    {
        var handle = *(uint*) (pawn + CBasePlayerPawn_m_hController_offset);

        if (handle == uint.MaxValue)
        {
            return -1;
        }

        var slot = (int) (handle & 0x7FFF) - 1;

        return slot < PlayerSlot.MaxPlayerCount ? slot : -1;
    }

    [UnmanagedCallersOnly]
    private static void hk_CCSPlayer_MovementServices_CategorizePosition(nint service, MoveData* mv, bool stayOnGround)
    {
        var pawn = *(nint*) (service + CPlayerPawnComponent_m_pChainEntity_offset);
        var slot = pawn != nint.Zero ? GetPlayerSlot(pawn) : -1;

        // stayOnGround is the pawn's FL_ONGROUND.
        if (stayOnGround || slot < 0 || IsOnGround(pawn))
        {
            CCSPlayer_MovementServices_CategorizePosition(service, mv, stayOnGround);

            return;
        }

        var velocity = mv->Velocity;
        var collided = ConsumeCollision(slot, velocity, out var expectedVelocity);

        CCSPlayer_MovementServices_CategorizePosition(service, mv, stayOnGround);

        var landed = IsOnGround(pawn);

        if (collided || landed)
        {
            RecordSpeedLoss(slot, expectedVelocity);
        }

        if (!landed || _instance is not { } module)
        {
            return;
        }

        // An exception escaping an unmanaged callback takes the server down with it.
        try
        {
            module.OnLanded(service, pawn, mv, velocity);
        }
        catch (Exception e)
        {
            module._logger.LogError(e, "Error while applying slopefix");
        }
    }

    [UnmanagedCallersOnly]
    private static void hk_CCSPlayer_MovementServices_TryPlayerMove(nint      service,
                                                                     MoveData* mv,
                                                                     nint      firstDest,
                                                                     nint      firstTrace,
                                                                     nint      isSurfing)
    {
        var pawn = *(nint*) (service + CPlayerPawnComponent_m_pChainEntity_offset);
        var slot = pawn != nint.Zero ? GetPlayerSlot(pawn) : -1;

        if (slot < 0 || IsOnGround(pawn))
        {
            CCSPlayer_MovementServices_TryPlayerMove(service, mv, firstDest, firstTrace, isSurfing);

            return;
        }

        var velocity = mv->Velocity;

        CCSPlayer_MovementServices_TryPlayerMove(service, mv, firstDest, firstTrace, isSurfing);

        RecordCollision(slot, velocity, mv->Velocity);
    }

    [UnmanagedCallersOnly]
    private static nint hk_CTriggerTeleport_Teleport(nint trigger, nint other)
    {
        var module  = _instance;
        var pending = default(PendingTelehop);

        if (module is not null)
        {
            try
            {
                pending = module.BeforeTriggerTeleport(other);
            }
            catch (Exception e)
            {
                module._logger.LogError(e, "Error while applying telehop fix");
            }
        }

        var result = CTriggerTeleport_Teleport(trigger, other);

        if (module is not null && pending.Pawn is not null)
        {
            try
            {
                AfterTriggerTeleport(pending);
            }
            catch (Exception e)
            {
                module._logger.LogError(e, "Error while applying telehop fix");
            }
        }

        return result;
    }
}
