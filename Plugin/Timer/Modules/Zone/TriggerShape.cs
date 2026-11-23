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
using SharpVector = Sharp.Shared.Types.Vector;
using Vector3 = System.Numerics.Vector3;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

// A trigger's shape, changed once it's made. Script_CreateTrigger's box set up drops the entity's physics, calls
// SetSolid(SOLID_BBOX) and builds the physics again from the collision shape; a SOLID_BBOX box stays on the world axes
// whatever the trigger's angles. The same three steps with another solid type give a SOLID_OBB box that turns with the
// trigger, or a SOLID_CYLINDER whose shape slot holds a hull of ours. Their addresses come from the box set up's code.
internal partial class ZoneModule
{
    // ReSharper disable InconsistentNaming
    private static unsafe delegate* unmanaged<nint, byte, void> CCollisionProperty_SetSolid;

    // (CBodyComponent*, CBaseEntity*): the entity's physics, built from its solid type.
    private static unsafe delegate* unmanaged<nint, nint, nint> BuildPhysics;

    private static unsafe delegate* unmanaged<int, Vector3*, nint> RnHullCreateRuntime;
    private static unsafe delegate* unmanaged<nint, void>          RnHullDestroy;

    // ReSharper restore InconsistentNaming

    // The entity's vtable entry, in bytes, that drops its physics.
    private static int _dropPhysicsOffset;

    // CCollisionProperty: the RnHull_t* a SOLID_CYLINDER's physics are built from, and the capsule SetSolid builds it from.
    private static int _shapeOffset;
    private static int _capsuleCenter1Offset;
    private static int _capsuleCenter2Offset;
    private static int _capsuleRadiusOffset;

    private bool _canTurn;
    private bool _canRebuild;

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
            _logger.LogWarning("Found no trigger box set up behind CreateTrigger, zones can't be turned and hollow map zones stay hollow");

            return;
        }

        CCollisionProperty_SetSolid = (delegate* unmanaged<nint, byte, void>) setSolid;
        BuildPhysics                = (delegate* unmanaged<nint, nint, nint>) calls.BuildPhysics;
        _dropPhysicsOffset          = calls.DropPhysics;
        _canTurn                    = true;
        _canRebuild                 = FindShapeSlot(setSolid);
    }

    private unsafe bool FindShapeSlot(ulong setSolid)
    {
        var vphysics = _bridge.Modules.VPhysics;
        RnHullCreateRuntime = (delegate* unmanaged<int, Vector3*, nint>) vphysics.GetExportFunction("RnHullCreateRuntime");
        RnHullDestroy       = (delegate* unmanaged<nint, void>) vphysics.GetExportFunction("RnHullDestroy");

        if (RnHullCreateRuntime == null || RnHullDestroy == null)
        {
            _logger.LogWarning("vphysics2 has no RnHullCreateRuntime or RnHullDestroy, hollow map zones stay hollow");

            return false;
        }

        var schema = _bridge.SchemaManager;
        _capsuleCenter1Offset = schema.GetNetVarOffset("CCollisionProperty", "m_vCapsuleCenter1");
        _capsuleCenter2Offset = schema.GetNetVarOffset("CCollisionProperty", "m_vCapsuleCenter2");
        _capsuleRadiusOffset  = schema.GetNetVarOffset("CCollisionProperty", "m_flCapsuleRadius");

        if (CollisionCode.ShapeSlot(setSolid, CollisionCode.Expected(_capsuleRadiusOffset), ReadCode) is not { } offset)
        {
            _logger.LogWarning("Found no collision shape slot in CCollisionProperty::SetSolid, hollow map zones stay hollow");

            return false;
        }

        _shapeOffset = offset;

        return true;
    }

    // Drops the trigger's physics and builds them again as `solid`; a SOLID_CYLINDER from `hull`, which the trigger then
    // owns. SetSolid builds a cylinder from the capsule first, so the slot is seen to fill (nothing before, something
    // after) before the hull takes its place. When it doesn't, the trigger gets its solid type and physics back, the
    // hull stays the caller's, and this returns false.
    private static unsafe bool SetShape(IBaseModelEntity entity, SolidType solid, nint hull = 0)
    {
        if (entity.GetCollisionProperty() is not { } collisionProperty || entity.GetBodyComponent() is not { } body)
        {
            return false;
        }

        var self      = entity.GetAbsPtr();
        var collision = collisionProperty.GetAbsPtr();
        var previous  = collisionProperty.SolidType;
        var slot      = (nint*) (collision + _shapeOffset);

        ((delegate* unmanaged<nint, void>) *(nint*) (*(nint*) self + _dropPhysicsOffset))(self);

        if (hull != 0)
        {
            *(SharpVector*) (collision + _capsuleCenter1Offset) = new SharpVector();
            *(SharpVector*) (collision + _capsuleCenter2Offset) = new SharpVector(0, 0, 1);
            *(float*) (collision + _capsuleRadiusOffset)        = 1;
        }

        var before = hull != 0 ? *slot : 0;

        CCollisionProperty_SetSolid(collision, (byte) solid);

        if (hull != 0)
        {
            if (before != 0 || *slot == 0)
            {
                CCollisionProperty_SetSolid(collision, (byte) previous);
                BuildPhysics(body.GetAbsPtr(), self);

                return false;
            }

            RnHullDestroy(*slot);
            *slot = hull;
        }

        BuildPhysics(body.GetAbsPtr(), self);

        return true;
    }
}
