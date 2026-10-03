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

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// The native detours the movement fixes share, and raw entity access. A native that can't be found only disables the
// fixes that need it.
internal unsafe partial class MovementFixModule
{
    // ReSharper disable InconsistentNaming

    private static delegate* unmanaged<nint, MoveData*, bool, void>             CCSPlayer_MovementServices_CategorizePosition;
    private static delegate* unmanaged<nint, MoveData*, nint, nint, nint, void> CCSPlayer_MovementServices_TryPlayerMove;
    private static delegate* unmanaged<nint, nint, nint>                        CTriggerTeleport_Teleport;

    private static delegate* unmanaged[SuppressGCTransition]<nint, TraceShapeRay*, Vector*, Vector*, CTraceFilter*, CGameTrace*, bool>
        CGamePhysicsQueryInterface_TraceShape;

    // ModSharp's natives, which handle GetAbsVelocity returning by value on Linux.
    private static delegate* unmanaged<nint, Vector*>       CBaseEntity_GetAbsOrigin;
    private static delegate* unmanaged<nint, Vector*>       CBaseEntity_GetAbsVelocity;
    private static delegate* unmanaged<nint, Vector*, void> CBaseEntity_SetAbsVelocity;

    private static nint g_pPhysicsQuery;
    private static nint CTraceFilterPlayerMovementCS_vtable;
    private static nint CCSPlayerPawn_vtable;

    private static int CPlayerPawnComponent_m_pChainEntity_offset;
    private static int CBaseEntity_m_lifeState_offset;
    private static int CBaseEntity_m_nActualMoveType_offset;
    private static int CBaseEntity_m_pCollision_offset;
    private static int CBaseEntity_m_hGroundEntity_offset;
    private static int CBaseEntity_m_flWaterLevel_offset;
    private static int CCollisionProperty_m_collisionAttribute_offset;
    private static int VPhysicsCollisionAttribute_t_m_nInteractsWith_offset;
    private static int VPhysicsCollisionAttribute_t_m_nHierarchyId_offset;
    private static int CBasePlayerPawn_m_hController_offset;
    private static int CCSPlayer_MovementServices_m_bDucked_offset;

    private const int CGlobalVars_frametime_offset = 0x34;

    // ReSharper restore InconsistentNaming

    private static bool _canTrace;
    private static bool _canSetVelocity;

    private void InstallHooks()
    {
        var schema = _bridge.SchemaManager;

        CPlayerPawnComponent_m_pChainEntity_offset = schema.GetNetVarOffset("CPlayerPawnComponent", "__m_pChainEntity");
        CBaseEntity_m_lifeState_offset             = schema.GetNetVarOffset("CBaseEntity", "m_lifeState");
        CBaseEntity_m_nActualMoveType_offset       = schema.GetNetVarOffset("CBaseEntity", "m_nActualMoveType");
        CBaseEntity_m_pCollision_offset            = schema.GetNetVarOffset("CBaseEntity", "m_pCollision");
        CBaseEntity_m_hGroundEntity_offset         = schema.GetNetVarOffset("CBaseEntity", "m_hGroundEntity");
        CBaseEntity_m_flWaterLevel_offset          = schema.GetNetVarOffset("CBaseEntity", "m_flWaterLevel");

        CCollisionProperty_m_collisionAttribute_offset
            = schema.GetNetVarOffset("CCollisionProperty", "m_collisionAttribute");

        VPhysicsCollisionAttribute_t_m_nInteractsWith_offset
            = schema.GetNetVarOffset("VPhysicsCollisionAttribute_t", "m_nInteractsWith");

        VPhysicsCollisionAttribute_t_m_nHierarchyId_offset
            = schema.GetNetVarOffset("VPhysicsCollisionAttribute_t", "m_nHierarchyId");

        CBasePlayerPawn_m_hController_offset        = schema.GetNetVarOffset("CBasePlayerPawn", "m_hController");
        CCSPlayer_MovementServices_m_bDucked_offset = schema.GetNetVarOffset("CCSPlayer_MovementServices", "m_bDucked");

        // All of these come from ModSharp's own gamedata.
        var gameData = _bridge.ModSharp.GetGameData();
        var server   = _bridge.Modules.Server;

        gameData.GetAddress("CGamePhysicsQueryInterface::TraceShape", out var traceShape);
        gameData.GetAddress("g_pPhysicsQuery", out g_pPhysicsQuery);
        server.TryGetVirtualTableByName("CTraceFilterPlayerMovementCS", out CTraceFilterPlayerMovementCS_vtable);

        CGamePhysicsQueryInterface_TraceShape
            = (delegate* unmanaged[SuppressGCTransition]<nint, TraceShapeRay*, Vector*, Vector*, CTraceFilter*, CGameTrace*, bool>)
            traceShape;

        _canTrace = traceShape != nint.Zero && g_pPhysicsQuery != nint.Zero && CTraceFilterPlayerMovementCS_vtable != nint.Zero;

        if (!_canTrace)
        {
            _logger.LogWarning("Failed to find TraceShape, g_pPhysicsQuery or CTraceFilterPlayerMovementCS, slopefix and the edgebug fix are disabled");
        }

        var getAbsOrigin   = _bridge.ModSharp.GetNativeFunctionPointer("Entity.GetAbsOrigin");
        var getAbsVelocity = _bridge.ModSharp.GetNativeFunctionPointer("Entity.GetAbsVelocity");
        var setAbsVelocity = _bridge.ModSharp.GetNativeFunctionPointer("Entity.SetAbsVelocity");
        server.TryGetVirtualTableByName("CCSPlayerPawn", out CCSPlayerPawn_vtable);

        CBaseEntity_GetAbsOrigin   = (delegate* unmanaged<nint, Vector*>) getAbsOrigin;
        CBaseEntity_GetAbsVelocity = (delegate* unmanaged<nint, Vector*>) getAbsVelocity;
        CBaseEntity_SetAbsVelocity = (delegate* unmanaged<nint, Vector*, void>) setAbsVelocity;

        _canSetVelocity = getAbsOrigin   != nint.Zero
                          && getAbsVelocity != nint.Zero
                          && setAbsVelocity != nint.Zero
                          && CCSPlayerPawn_vtable != nint.Zero;

        if (!_canSetVelocity)
        {
            _logger.LogWarning("Failed to find the CBaseEntity velocity accessors or the CCSPlayerPawn vtable, the telehop fix is disabled");
        }

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

#region Raw entity access

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsOnGround(nint entity)
        => *(uint*) (entity + CBaseEntity_m_hGroundEntity_offset) != uint.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAlive(nint entity)
        => *(LifeState*) (entity + CBaseEntity_m_lifeState_offset) == LifeState.Alive;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static MoveType GetMoveType(nint entity)
        => *(MoveType*) (entity + CBaseEntity_m_nActualMoveType_offset);

    // Where CS2 switches to WaterMove, (int) (level * 4 + 1) >= 3, like rngfix stopping at waist deep.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsInWater(nint entity)
        => *(float*) (entity + CBaseEntity_m_flWaterLevel_offset) >= 0.5f;

    // The controller's entity index is its slot + 1.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    // CEntityIdentity::GetRefEHandle.
    private static uint GetRefHandle(nint entity)
    {
        var identity = *(nint*) (entity + 0x10);

        if (identity == nint.Zero)
        {
            return uint.MaxValue;
        }

        var handle = *(uint*) (identity + 0x10);
        var flags  = *(uint*) (identity + 0x30);

        var index = handle != uint.MaxValue ? handle & 0x7FFF : 0x7FFF;

        // Debug builds check arithmetic, and the serial wraps on purpose.
        var serial = unchecked(((handle >> 15) - (flags & 0x1)) << 15);

        return index | serial;
    }

    private static TraceShapeRay CreatePlayerHull(nint service)
        => new (new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, *(bool*) (service + CCSPlayer_MovementServices_m_bDucked_offset) ? 54 : 72),
        });

    // The game's CTraceFilterPlayerMovementCS, ignoring the player.
    private static bool InitPlayerMovementFilter(CTraceFilter* filter, nint pawn)
    {
        var collision = *(nint*) (pawn + CBaseEntity_m_pCollision_offset);

        if (collision == nint.Zero)
        {
            return false;
        }

        var attribute = collision + CCollisionProperty_m_collisionAttribute_offset;

        *filter = default;

        filter->QueryAttribute = RnQueryShapeAttr.PlayerMovement(
            *(InteractionLayers*) (attribute + VPhysicsCollisionAttribute_t_m_nInteractsWith_offset));

        filter->QueryAttribute.m_nEntityIdsToIgnore[0] = GetRefHandle(pawn);
        filter->QueryAttribute.m_nHierarchyIds[0]
            = *(ushort*) (attribute + VPhysicsCollisionAttribute_t_m_nHierarchyId_offset);

        filter->Vtable             = (CTraceFilterVirtualTableDescriptor*) CTraceFilterPlayerMovementCS_vtable;
        filter->m_bIterateEntities = true;

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TracePlayerBBox(Vector* start, Vector* end, TraceShapeRay* ray, CTraceFilter* filter, CGameTrace* trace)
        => CGamePhysicsQueryInterface_TraceShape(g_pPhysicsQuery, ray, start, end, filter, trace);

#endregion

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

        if (landed)
        {
            ApplySlopeFix(service, pawn, slot, mv, velocity);
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

        ApplyEdgebugFix(service, pawn, slot, mv);

        var velocity = mv->Velocity;

        CCSPlayer_MovementServices_TryPlayerMove(service, mv, firstDest, firstTrace, isSurfing);

        RecordCollision(slot, velocity, mv->Velocity);
    }

    [UnmanagedCallersOnly]
    private static nint hk_CTriggerTeleport_Teleport(nint trigger, nint other)
    {
        var pending = BeforeTriggerTeleport(other);
        var result  = CTriggerTeleport_Teleport(trigger, other);

        if (pending.Pawn != nint.Zero)
        {
            AfterTriggerTeleport(pending);
        }

        return result;
    }
}
