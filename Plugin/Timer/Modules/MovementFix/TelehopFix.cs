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
using Sharp.Shared.Units;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// rngfix's telehop fix: give back the speed a collision or landing took in the tick a trigger_teleport fires. CS2 turns
// the velocity towards the destination during the teleport, so it goes back onto the pawn right before.
internal unsafe partial class MovementFixModule
{
    // The airborne TryPlayerMove of the current step clipped the velocity.
    private static readonly bool[]   _collided                = new bool[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _velocityBeforeCollision = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _velocityAfterCollision  = new Vector[PlayerSlot.MaxPlayerCount];

    private static readonly int[]    _speedLossTick    = new int[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _expectedVelocity = new Vector[PlayerSlot.MaxPlayerCount];

    private static readonly int[] _teleportTick = new int[PlayerSlot.MaxPlayerCount];

    private readonly record struct PendingTelehop(nint   Pawn,
                                                  int    Slot,
                                                  int    Tick,
                                                  Vector Origin,
                                                  Vector Velocity,
                                                  bool   Restored);

    private static void RecordCollision(int slot, Vector before, Vector after)
    {
        // Every clip changes the velocity by at least 1/32.
        if (_collided[slot] || (after - before).LengthSqr() < 1e-4f)
        {
            return;
        }

        _collided[slot]                = true;
        _velocityBeforeCollision[slot] = before;
        _velocityAfterCollision[slot]  = after;
    }

    // What changed after TryPlayerMove (the second half of gravity, the base velocity) still applies to the expected
    // velocity.
    private static bool ConsumeCollision(int slot, Vector velocity, out Vector expected)
    {
        if (!_collided[slot])
        {
            expected = velocity;

            return false;
        }

        _collided[slot] = false;
        expected        = _velocityBeforeCollision[slot] + (velocity - _velocityAfterCollision[slot]);

        return true;
    }

    private static void RecordSpeedLoss(int slot, Vector expected)
    {
        if (_speedLossTick[slot] == _moveTick[slot])
        {
            return;
        }

        _speedLossTick[slot]    = _moveTick[slot];
        _expectedVelocity[slot] = expected;
    }

    // Shared by the teleport detour and the trigger jump fix.
    private static nint RunTriggerTeleport(nint trigger, nint other, out bool teleported)
    {
        var pending = BeforeTriggerTeleport(other);
        var result  = CTriggerTeleport_Teleport(trigger, other);

        teleported = pending.Pawn != nint.Zero && AfterTriggerTeleport(pending);

        return result;
    }

    private static PendingTelehop BeforeTriggerTeleport(nint other)
    {
        if (!_canSetVelocity || other == nint.Zero || *(nint*) other != CCSPlayerPawn_vtable)
        {
            return default;
        }

        var slot = GetPlayerSlot(other);

        if (slot < 0 || _isFakeClient[slot] || !IsAlive(other))
        {
            return default;
        }

        var tick = _moveTick[slot];

        // Not again in the same tick, and not right after another teleport (rngfix's teleport hub guard).
        var restore = _telehopEnabled
                      && GetMoveType(other) == MoveType.Walk
                      && !IsInWater(other)
                      && _speedLossTick[slot] == tick
                      && _teleportTick[slot]  != tick
                      && _teleportTick[slot]  != tick - 1;

        var velocity = *CBaseEntity_GetAbsVelocity(other);

        if (restore)
        {
            var expected = _expectedVelocity[slot];
            CBaseEntity_SetAbsVelocity(other, &expected);
        }

        return new (other, slot, tick, *CBaseEntity_GetAbsOrigin(other), velocity, restore);
    }

    private static bool AfterTriggerTeleport(in PendingTelehop pending)
    {
        if (*CBaseEntity_GetAbsOrigin(pending.Pawn) != pending.Origin)
        {
            _teleportTick[pending.Slot] = pending.Tick;

            return true;
        }

        // The destination didn't exist.
        if (pending.Restored)
        {
            var velocity = pending.Velocity;
            CBaseEntity_SetAbsVelocity(pending.Pawn, &velocity);
        }

        return false;
    }
}
