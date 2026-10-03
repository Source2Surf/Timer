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

using System.Runtime.InteropServices;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace Source2Surf.Timer.Native;

// Writable trace_t for calling CGamePhysicsQueryInterface::TraceShape directly, copied from RampFix.
[StructLayout(LayoutKind.Explicit)]
internal unsafe ref struct CGameTrace
{
    [FieldOffset(0)]
    public PhysicsSurfaceProperties* SurfaceProp;

    [FieldOffset(8)]
    public nint Entity;

    [FieldOffset(16)]
    public HitBoxData* HitBoxData;

    [FieldOffset(24)]
    public nint PhysicsBody;

    [FieldOffset(32)]
    public nint PhysicsShape;

    [FieldOffset(40)]
    public uint Contents;

    [FieldOffset(80)]
    public RnCollisionAttr ShapeAttributes;

    [FieldOffset(120)]
    public Vector StartPosition;

    [FieldOffset(132)]
    public Vector EndPosition;

    [FieldOffset(144)]
    public Vector PlaneNormal;

    [FieldOffset(156)]
    public Vector HitPoint;

    [FieldOffset(168)]
    public float HitOffset;

    [FieldOffset(172)]
    public float Fraction;

    [FieldOffset(180)]
    public float Triangle;

    [FieldOffset(184)]
    public short HitBoxBoneIndex;

    [FieldOffset(186)]
    public TraceRayType RayType;

    [FieldOffset(187)]
    public bool StartInSolid;

    [FieldOffset(188)]
    public bool ExactHitPoint;

    public bool DidHit()
        => Fraction < 1.0f || StartInSolid;
}
