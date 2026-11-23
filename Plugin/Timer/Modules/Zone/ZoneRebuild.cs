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
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Zone;
using Vector3 = System.Numerics.Vector3;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

// A map zone built as shells around an empty inside holds a player only while they touch its faces: a jump inside it
// leaves it, so a start zone starts the timer in the air and restarts it on landing. Such a zone's trigger gets one solid
// convex hull around its points instead, when its footprint is convex; open meshes, thin plates and concave footprints
// are logged. A closed mesh counts its inside and stays as it is.
internal partial class ZoneModule
{
    private readonly IConVar? timer_zone_solidify;

    // A map zone just spawned: looked at on the next frame, once the map has spawned.
    private void RebuildIfHollowLater(uint handle)
    {
        if (_canRebuild && timer_zone_solidify?.GetBool() != false)
        {
            _bridge.ModSharp.InvokeFrameAction(() => RebuildIfHollow(handle));
        }
    }

    private unsafe void RebuildIfHollow(uint handle)
    {
        if (!_canRebuild
            || !_zones.TryGetValue(handle, out var info)
            || !info.Prebuilt
            || _bridge.EntityManager.FindEntityByIndex<IBaseModelEntity>(info.Index) is not { IsValidEntity: true } entity
            || entity.Handle.GetValue() != handle
            || entity.GetBodyComponent()?.GetSceneNode() is not { } node)
        {
            return;
        }

        var name = info.TargetName;

        // The hull is in the model's space, unscaled; a trigger that moves with something hasn't been tried.
        if (node.GetParent() is not null || MathF.Abs(node.AbsScale - 1f) > 0.001f)
        {
            return;
        }

        if (ReadShape(node.AsSkeletonInstance?.GetModelState().ModelName) is not var (hulls, meshes) || hulls.Count + meshes.Count == 0)
        {
            return;
        }

        var report = ZoneSolidityCheck.Measure(hulls, meshes);

        if (report.HoldsInside)
        {
            return;
        }

        if (!report.CanRebuildConvex)
        {
            _logger.LogWarning("{Map}: zone {Zone} is {Kind} (hollow {Hollow:0}%, footprint {Convex:0}% convex); a jump inside it can leave it. "
                               + "It stays as the map made it: one hull around it would change its shape",
                               _bridge.CurrentMapName, name, report.Kind, report.HollowPercent, report.FootprintConvexPercent);

            return;
        }

        var points = new List<Vector3>();

        foreach (var hull in hulls)
        {
            points.AddRange(hull.Vertices);
        }

        foreach (var mesh in meshes)
        {
            points.AddRange(mesh.Vertices);
        }

        var  pointArray = points.ToArray();
        nint solid;

        fixed (Vector3* p = pointArray)
        {
            solid = RnHullCreateRuntime(pointArray.Length, p);
        }

        if (solid == 0)
        {
            _logger.LogWarning("{Map}: no hull could be built around zone {Zone}, it stays {Kind}", _bridge.CurrentMapName, name, report.Kind);

            return;
        }

        if (!SetShape(entity, SolidType.Cylinder, solid))
        {
            RnHullDestroy(solid);
            _canRebuild = false;
            _logger.LogError("The collision shape slot didn't fill as expected, hollow map zones stay hollow");

            return;
        }

        _logger.LogInformation("{Map}: zone {Zone} was {Kind} (hollow {Hollow:0}%), made solid", _bridge.CurrentMapName, name,
                               report.Kind, report.HollowPercent);
    }

    // The convex pieces and meshes of a model's physics, in its own space.
    private (List<SolidHull> Hulls, List<SolidMesh> Meshes)? ReadShape(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return null;
        }

        var kv = _bridge.ModSharp.CreateKeyValues3(KeyValues3Type.Null, KeyValues3SubType.Null);

        try
        {
            if (!kv.LoadFromCompiledFile(model, "GAME", ResourceBlockType.PHYS, out _) || kv.FindMember("m_parts") is not { } parts)
            {
                return null;
            }

            var hulls  = new List<SolidHull>();
            var meshes = new List<SolidMesh>();

            for (var p = 0; p < parts.GetArrayElementCount(); p++)
            {
                if (parts.GetArrayElement(p)?.FindMember("m_rnShape") is not { } shape)
                {
                    continue;
                }

                if (shape.FindMember("m_hulls") is { } hullList)
                {
                    for (var i = 0; i < hullList.GetArrayElementCount(); i++)
                    {
                        var hull     = hullList.GetArrayElement(i)?.FindMember("m_Hull");
                        var planes   = Blob(hull, "m_Planes");
                        var vertices = Blob(hull, "m_VertexPositions");

                        if (!planes.IsEmpty && !vertices.IsEmpty)
                        {
                            hulls.Add(new SolidHull(MemoryMarshal.Cast<byte, SolidPlane>(planes).ToArray(),
                                                    MemoryMarshal.Cast<byte, Vector3>(vertices).ToArray()));
                        }
                    }
                }

                if (shape.FindMember("m_meshes") is { } meshList)
                {
                    for (var i = 0; i < meshList.GetArrayElementCount(); i++)
                    {
                        var mesh      = meshList.GetArrayElement(i)?.FindMember("m_Mesh");
                        var vertices  = Blob(mesh, "m_Vertices");
                        var triangles = Blob(mesh, "m_Triangles");

                        if (!vertices.IsEmpty && !triangles.IsEmpty)
                        {
                            meshes.Add(new SolidMesh(MemoryMarshal.Cast<byte, Vector3>(vertices).ToArray(),
                                                     MemoryMarshal.Cast<byte, int>(triangles).ToArray()));
                        }
                    }
                }
            }

            return (hulls, meshes);
        }
        finally
        {
            kv.DeleteThis();
        }
    }

    private static ReadOnlySpan<byte> Blob(IKeyValues3? parent, string member)
        => parent?.FindMember(member) is { } kv ? kv.GetBinaryBlob() : default;
}
