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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Source2Surf.Timer.Modules.Zone;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

// A trigger's shape, changed once it's made. Script_CreateTrigger's box set up drops the entity's physics, calls
// SetSolid(SOLID_BBOX) and builds the physics again from the collision shape; a SOLID_BBOX box stays on the world axes
// whatever the trigger's angles. The same three steps with SOLID_OBB give a box that turns with the trigger. Their
// addresses come from the box set up's code.
internal partial class ZoneModule
{
    // ReSharper disable InconsistentNaming
    private static unsafe delegate* unmanaged<nint, byte, void> CCollisionProperty_SetSolid;

    // (CBodyComponent*, CBaseEntity*): the entity's physics, built from its solid type.
    private static unsafe delegate* unmanaged<nint, nint, nint> BuildPhysics;

    // ReSharper restore InconsistentNaming

    // The entity's vtable entry, in bytes, that drops its physics.
    private static int _dropPhysicsOffset;

    private bool _canTurn;

    private static readonly Func<ulong, int, byte[]?> ReadCode = static (address, length) =>
    {
        var code = new byte[length];
        Marshal.Copy((nint) address, code, 0, length);

        return code;
    };

    // Once CreateTrigger is found: the box set up is found through it.
    private unsafe void FindShapeCalls()
    {
        var setSolid = (ulong) _bridge.ModSharp.GetGameData().GetAddress("CCollisionProperty::SetSolid");

        if (setSolid == 0
            || CollisionCode.BoxInit((ulong) CreateTrigger, setSolid, CollisionCode.SetSolidBody(setSolid, ReadCode), ReadCode) is not { } boxInit
            || CollisionCode.PhysicsCalls(boxInit, ReadCode) is not { } calls)
        {
            _logger.LogWarning("Found no trigger box set up behind CreateTrigger, zones can't be turned");

            return;
        }

        CCollisionProperty_SetSolid = (delegate* unmanaged<nint, byte, void>) setSolid;
        BuildPhysics                = (delegate* unmanaged<nint, nint, nint>) calls.BuildPhysics;
        _dropPhysicsOffset          = calls.DropPhysics;
        _canTurn                    = true;
    }

    // Drops the trigger's physics and builds them again as `solid`.
    private static unsafe void SetShape(IBaseModelEntity entity, SolidType solid)
    {
        if (entity.GetCollisionProperty() is not { } collisionProperty || entity.GetBodyComponent() is not { } body)
        {
            return;
        }

        var self = entity.GetAbsPtr();

        ((delegate* unmanaged<nint, void>) *(nint*) (*(nint*) self + _dropPhysicsOffset))(self);
        CCollisionProperty_SetSolid(collisionProperty.GetAbsPtr(), (byte) solid);
        BuildPhysics(body.GetAbsPtr(), self);
    }
}
