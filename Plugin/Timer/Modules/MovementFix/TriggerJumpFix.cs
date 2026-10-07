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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's trigger jump fix. CS2 lands a player while they still hover up to 2 units above the ground, and triggers fire
// from where the player ends the tick, so landing and jumping straight away skips triggers lying in that gap. After the
// tick, start touching the ones there the game won't touch itself, and end it on the player's next tick, as a physics
// touch of one tick does (and Source 1's touch links did).
internal unsafe partial class MovementFixModule
{
    private const int MaxTriggerHits = 16;

    private static readonly int[]    _landTick   = new int[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _landOrigin = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   _landDucked = new bool[PlayerSlot.MaxPlayerCount];

    // By slot: the touches to end on the player's next tick, by handle (a trigger_once removes itself).
    private static readonly uint[] _touchingPawn     = new uint[PlayerSlot.MaxPlayerCount];
    private static readonly uint[] _touchingTriggers = new uint[(int) PlayerSlot.MaxPlayerCount * MaxTriggerHits];
    private static readonly int[]  _touchingCount    = new int[PlayerSlot.MaxPlayerCount];

    // Filled by TriggerFilterShouldHitEntity while a trigger query runs.
    private static readonly nint[] _triggerHits = new nint[MaxTriggerHits];
    private static          int    _triggerHitCount;

    private static CTraceFilterVirtualTableDescriptor* _triggerFilterVtable;

    private static void CreateTriggerFilterVtable()
    {
        _triggerFilterVtable = (CTraceFilterVirtualTableDescriptor*) NativeMemory.Alloc((nuint) sizeof(CTraceFilterVirtualTableDescriptor));

        _triggerFilterVtable->Deconstructor   = &TriggerFilterDestructor;
        _triggerFilterVtable->ShouldHitEntity = &TriggerFilterShouldHitEntity;
    }

    private static void FreeTriggerFilterVtable()
    {
        NativeMemory.Free(_triggerFilterVtable);
        _triggerFilterVtable = null;
    }

    private static ReadOnlySpan<byte> DesignerName(nint entity)
    {
        var identity = *(nint*) (entity + 0x10);
        var name     = identity != nint.Zero ? *(byte**) (identity + CEntityIdentity_m_designerName_offset) : null;

        return name == null ? default : MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
    }

    // Teleports, boosters, and zones and map logic.
    private static bool IsTrigger(nint entity)
    {
        var classname = DesignerName(entity);

        return classname.SequenceEqual("trigger_teleport"u8)
               || classname.SequenceEqual("trigger_push"u8)
               || classname.SequenceEqual("trigger_multiple"u8)
               || classname.SequenceEqual("trigger_once"u8);
    }

    private static void RecordLanding(int slot, Vector origin, bool ducked)
    {
        _landTick[slot]   = _moveTick[slot];
        _landOrigin[slot] = origin;
        _landDucked[slot] = ducked;
    }

    // Movement has been written back to the pawn by PostThink, so a teleport sticks.
    private static void OnPlayerPostThink(IPlayerThinkForwardParams @params)
    {
        int slot = @params.Client.Slot;

        EndTouches(slot);

        if (_bhopBlocks.Count > 0 && !_isFakeClient[slot])
        {
            ApplyBhopBlocks(@params.Pawn.GetAbsPtr(), slot);
        }

        if (_landTick[slot] != _moveTick[slot])
        {
            return;
        }

        _landTick[slot] = int.MinValue;

        if (_triggerJumpEnabled && _canTriggerJump && !_isFakeClient[slot])
        {
            ApplyTriggerJumpFix(@params.Pawn.GetAbsPtr(), slot);
        }
    }

    private static void ApplyTriggerJumpFix(nint pawn, int slot)
    {
        if (!IsAlive(pawn) || GetMoveType(pawn) != MoveType.Walk || IsInWater(pawn))
        {
            return;
        }

        var filter = stackalloc CTraceFilter[1];

        if (!InitPlayerMovementFilter(filter, pawn))
        {
            return;
        }

        var origin = _landOrigin[slot];
        var height = _landDucked[slot] ? 54.0f : 72.0f;
        var hull   = new TraceShapeRay(new TraceShapeHull { Mins = new (-16, -16, 0), Maxs = new (16, 16, height) });
        var below  = origin;
        below.Z -= LandHeight;

        var trace = stackalloc CGameTrace[1];
        TracePlayerBBox(&origin, &below, &hull, filter, trace);

        if (trace->StartInSolid || trace->Fraction <= 0.0f || trace->Fraction >= 1.0f)
        {
            return;
        }

        // The gap under the hull, reaching 1/32 into the ground for triggers flush with it.
        var bottom   = trace->EndPosition.Z - 0.03125f;
        var gapPoint = new Vector(origin.X, origin.Y, bottom);

        var gap = new TraceShapeRay(new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, origin.Z - bottom),
        });

        var gapHits  = stackalloc nint[MaxTriggerHits];
        var gapCount = CollectTriggers(&gap, gapPoint, gapHits);

        if (gapCount == 0)
        {
            return;
        }

        // The game touches whatever the hull overlaps at the end of the tick itself.
        var current      = *CBaseEntity_GetAbsOrigin(pawn);
        var currentHits  = stackalloc nint[MaxTriggerHits];
        var currentCount = CollectTriggers(&hull, current, currentHits);

        var handle = GetRefHandle(pawn);

        for (var i = 0; i < gapCount; i++)
        {
            var trigger = gapHits[i];

            // CBaseTrigger::StartTouch fires its outputs again for a player it already lists.
            if (Contains(currentHits, currentCount, trigger)
                || !CanTouch(trigger, pawn, handle)
                || IsMarkedForDeletion(trigger)
                || IsMarkedForDeletion(pawn))
            {
                continue;
            }

            // What the physics does for a pair that starts touching.
            CallTouch(trigger, CBaseEntity_StartTouch_index, pawn);
            CallTouch(trigger, CBaseEntity_Touch_index, pawn);
            CallTouch(pawn, CBaseEntity_StartTouch_index, trigger);
            CallTouch(pawn, CBaseEntity_Touch_index, trigger);

            _touchingPawn[slot] = handle;
            _touchingTriggers[slot * MaxTriggerHits + _touchingCount[slot]++] = GetRefHandle(trigger);
        }
    }

    // The player's next tick: what the physics does for a pair that stops touching.
    private static void EndTouches(int slot)
    {
        var count = _touchingCount[slot];

        if (count == 0)
        {
            return;
        }

        _touchingCount[slot] = 0;

        var pawn = EntityFromHandle(_touchingPawn[slot]);

        if (pawn == nint.Zero)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var trigger = EntityFromHandle(_touchingTriggers[slot * MaxTriggerHits + i]);

            if (trigger == nint.Zero)
            {
                continue;
            }

            CallTouch(trigger, CBaseEntity_EndTouch_index, pawn);
            CallTouch(pawn, CBaseEntity_EndTouch_index, trigger);
        }
    }

    private static nint EntityFromHandle(uint handle)
        => _entityManager.FindEntityByHandle(new CEntityHandle<IBaseEntity>(handle))?.GetAbsPtr() ?? nint.Zero;

    private static void CallTouch(nint entity, int index, nint other)
        => ((delegate* unmanaged<nint, nint, void>) (*(nint**) entity)[index])(entity, other);

    // Every trigger the hull overlaps at point.
    private static int CollectTriggers(TraceShapeRay* ray, Vector point, nint* hits)
    {
        var filter = stackalloc CTraceFilter[1];

        *filter = default;

        filter->QueryAttribute = new RnQueryShapeAttr
        {
            m_nInteractsWith  = InteractionLayers.Trigger,
            m_nCollisionGroup = CollisionGroupType.Debris,
            HitTrigger        = true,
        };

        filter->Vtable             = _triggerFilterVtable;
        filter->m_bIterateEntities = true;

        _triggerHitCount = 0;

        var trace = stackalloc CGameTrace[1];
        CGamePhysicsQueryInterface_TraceShape_ManagedFilter(g_pPhysicsQuery, ray, &point, &point, filter, trace);

        var count = _triggerHitCount;

        for (var i = 0; i < count; i++)
        {
            hits[i] = _triggerHits[i];
        }

        return count;
    }

    private static bool Contains(nint* entities, int count, nint entity)
    {
        for (var i = 0; i < count; i++)
        {
            if (entities[i] == entity)
            {
                return true;
            }
        }

        return false;
    }

    // What the game checks before StartTouch.
    private static bool CanTouch(nint trigger, nint pawn, uint pawnHandle)
    {
        if (*(bool*) (trigger + CBaseTrigger_m_bDisabled_offset))
        {
            return false;
        }

        // CUtlVector: the count, then the elements.
        var touching = trigger + CBaseTrigger_m_hTouchingEntities_offset;
        var count    = *(int*) touching;
        var handles  = *(uint**) (touching + 8);

        for (var i = 0; i < count; i++)
        {
            if (handles[i] == pawnHandle)
            {
                return false;
            }
        }

        return PassesTriggerFilters(trigger, pawn);
    }

    private static bool PassesTriggerFilters(nint trigger, nint pawn)
        => ((delegate* unmanaged<nint, nint, bool>) (*(nint**) trigger)[CBaseTrigger_PassesTriggerFilters_index])(trigger, pawn);

    [UnmanagedCallersOnly]
    private static void TriggerFilterDestructor(nint filter)
    {
    }

    [UnmanagedCallersOnly]
    private static bool TriggerFilterShouldHitEntity(CTraceFilter* filter, nint entity)
    {
        if (entity != nint.Zero && _triggerHitCount < MaxTriggerHits && IsTrigger(entity))
        {
            _triggerHits[_triggerHitCount++] = entity;
        }

        // Keep going, so every trigger gets reported.
        return false;
    }
}
