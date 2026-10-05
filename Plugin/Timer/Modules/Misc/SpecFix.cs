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
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// Valve's IsValidObserverTarget lets a respawning (dead) pawn through, so spec_next can land on a dead player.
// Only pawns alive on T or CT are valid; a dying current target keeps Valve's death cam, except the spectator's own
// pawn, which joining spectators from a team would otherwise watch.
internal unsafe partial class MiscModule
{
    // ReSharper disable InconsistentNaming

    private static delegate* unmanaged<nint, nint, byte> CCSObserver_ObserverServices_IsValidObserverTarget;

    private static nint g_CCSPlayerPawn_vtable;
    private static int  CBaseEntity_m_lifeState_offset;
    private static int  CBaseEntity_m_iTeamNum_offset;
    private static int  CPlayer_ObserverServices_m_hObserverTarget_offset;
    private static int  CPlayerPawnComponent_m_pChainEntity_offset;
    private static int  CBasePlayerPawn_m_hController_offset;

    // ReSharper restore InconsistentNaming

    private void InstallSpecFix()
    {
        var schema = _bridge.SchemaManager;

        CBaseEntity_m_lifeState_offset = schema.GetNetVarOffset("CBaseEntity", "m_lifeState");
        CBaseEntity_m_iTeamNum_offset  = schema.GetNetVarOffset("CBaseEntity", "m_iTeamNum");

        CPlayer_ObserverServices_m_hObserverTarget_offset
            = schema.GetNetVarOffset("CPlayer_ObserverServices", "m_hObserverTarget");

        CPlayerPawnComponent_m_pChainEntity_offset = schema.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBasePlayerPawn_m_hController_offset       = schema.GetNetVarOffset("CBasePlayerPawn", "m_hController");

        var target = FindIsValidObserverTarget();

        if (target == nint.Zero || !_bridge.Modules.Server.TryGetVirtualTableByName("CCSPlayerPawn", out g_CCSPlayerPawn_vtable))
        {
            _logger.LogWarning("Failed to find CCSObserver_ObserverServices::IsValidObserverTarget, spectators can still pick dead players");

            return;
        }

        if (!_inlineHookManager.AddHook(target,
                                        (nint) (delegate* unmanaged<nint, nint, byte>) (&hk_IsValidObserverTarget),
                                        out var trampoline))
        {
            _logger.LogWarning("Failed to hook CCSObserver_ObserverServices::IsValidObserverTarget, spectators can still pick dead players");

            return;
        }

        CCSObserver_ObserverServices_IsValidObserverTarget = (delegate* unmanaged<nint, nint, byte>) trampoline;
    }

    // The scan survives updates that move the vtable index, and the gamedata index backs it up. When they disagree, one
    // of them found the wrong function, so neither is used.
    private nint FindIsValidObserverTarget()
    {
        var virtuals = _bridge.Modules.Server.GetVirtualFunctions("CCSObserver_ObserverServices");

        if (virtuals.Length == 0)
        {
            return nint.Zero;
        }

        var scanned = ScanIsValidObserverTarget(virtuals);

        var indexed = _bridge.ModSharp.GetGameData().GetVFuncIndex("CCSObserver_ObserverServices::IsValidObserverTarget", out var index)
                      && index >= 0
                      && index < virtuals.Length
            ? virtuals[index]
            : nint.Zero;

        if (scanned != nint.Zero && indexed != nint.Zero && scanned != indexed)
        {
            _logger.LogError("CCSObserver_ObserverServices::IsValidObserverTarget: heuristic scan found 0x{Scanned:X} but the gamedata index found 0x{Indexed:X}",
                             scanned,
                             indexed);

            return nint.Zero;
        }

        return scanned != nint.Zero ? scanned : indexed;
    }

    // It special-cases a planted C4, so it is the only observer-services virtual referencing CPlantedC4's RTTI (index 34
    // on Windows and 35 on Linux, 2026-10).
    private nint ScanIsValidObserverTarget(nint[] virtuals)
    {
        var server = _bridge.Modules.Server;
        var found  = nint.Zero;

        foreach (var rtti in FindPlantedC4Rtti(server))
        {
            foreach (var reference in server.GetReferencesFromPointer(rtti))
            {
                if (!server.GetFunctionRange(reference, out var start, out _) || Array.IndexOf(virtuals, start) < 0)
                {
                    continue;
                }

                if (found != nint.Zero && found != start)
                {
                    return nint.Zero;
                }

                found = start;
            }
        }

        return found;
    }

    // MSVC's TypeDescriptor sits 16 bytes before its name; an Itanium type_info holds its name after its vtable pointer.
    private static IEnumerable<nint> FindPlantedC4Rtti(ILibraryModule server)
    {
        if (OperatingSystem.IsWindows())
        {
            var descriptorName = server.FindString(".?AVCPlantedC4@@", false, true);

            return descriptorName != nint.Zero ? [descriptorName - 0x10] : [];
        }

        var typeName = server.FindStringExact("10CPlantedC4");

        return typeName != nint.Zero ? server.FindPointers(typeName).Select(p => p - 8) : [];
    }

    [UnmanagedCallersOnly]
    private static byte hk_IsValidObserverTarget(nint service, nint target)
    {
        var valid = CCSObserver_ObserverServices_IsValidObserverTarget(service, target);

        if (valid == 0 || target == nint.Zero || *(nint*) target != g_CCSPlayerPawn_vtable)
        {
            return valid;
        }

        if (IsOwnPawn(service, target))
        {
            return 0;
        }

        var lifeState = (LifeState) (*(byte*) (target + CBaseEntity_m_lifeState_offset));

        if (lifeState == LifeState.Alive)
        {
            return *(byte*) (target + CBaseEntity_m_iTeamNum_offset) >= (byte) CStrikeTeam.TE ? valid : (byte) 0;
        }

        return lifeState is LifeState.Dying or LifeState.Dead && IsObserverTarget(service, target) ? valid : (byte) 0;
    }

    // The observer pawn owning the services and the target pawn belong to the same controller.
    private static bool IsOwnPawn(nint service, nint target)
    {
        var observer = *(nint*) (service + CPlayerPawnComponent_m_pChainEntity_offset);

        if (observer == nint.Zero)
        {
            return false;
        }

        var controller = *(uint*) (observer + CBasePlayerPawn_m_hController_offset);

        return controller != uint.MaxValue
               && (controller & 0x7FFF) == (*(uint*) (target + CBasePlayerPawn_m_hController_offset) & 0x7FFF);
    }

    // Compares entity indices: CEntityInstance::m_pEntity, then CEntityIdentity's handle.
    private static bool IsObserverTarget(nint service, nint target)
    {
        var identity = *(nint*) (target + 0x10);

        return identity != nint.Zero
               && (*(uint*) (service + CPlayer_ObserverServices_m_hObserverTarget_offset) & 0x7FFF) == (*(uint*) (identity + 0x10) & 0x7FFF);
    }
}
