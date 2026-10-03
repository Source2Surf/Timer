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

using Sharp.Shared.Types;
using Sharp.Shared.Units;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// What the airborne TryPlayerMove of the current movement step did, for slopefix and the telehop fix.
internal unsafe partial class MovementFixModule
{
    private static readonly bool[]   _moved                  = new bool[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _moveVelocity           = new Vector[PlayerSlot.MaxPlayerCount];
    private static readonly bool[]   _collided               = new bool[PlayerSlot.MaxPlayerCount];
    private static readonly Vector[] _velocityAfterCollision = new Vector[PlayerSlot.MaxPlayerCount];

    // ExpectedVelocity is what the step would have ended with had TryPlayerMove not run into anything.
    private readonly record struct StepMove(bool Moved, Vector MoveVelocity, bool Collided, Vector ExpectedVelocity);

    private static void RecordStepMove(int slot, Vector before, Vector after)
    {
        if (_moved[slot])
        {
            return;
        }

        _moved[slot]        = true;
        _moveVelocity[slot] = before;

        // Every clip changes the velocity by at least 1/32.
        if ((after - before).LengthSqr() >= 1e-4f)
        {
            _collided[slot]               = true;
            _velocityAfterCollision[slot] = after;
        }
    }

    // What changed after TryPlayerMove (the second half of gravity, the base velocity) still applies to the expected
    // velocity.
    private static StepMove TakeStepMove(int slot, Vector velocity)
    {
        var moved    = _moved[slot];
        var collided = moved && _collided[slot];

        _moved[slot]    = false;
        _collided[slot] = false;

        var expected = collided ? _moveVelocity[slot] + (velocity - _velocityAfterCollision[slot]) : velocity;

        return new (moved, _moveVelocity[slot], collided, expected);
    }
}
