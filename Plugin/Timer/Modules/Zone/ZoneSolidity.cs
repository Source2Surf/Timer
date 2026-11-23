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
using System.Numerics;

namespace Source2Surf.Timer.Modules.Zone;

// A face of a convex piece of a trigger's physics, as Rubikon stores it (RnPlane_t): inside is Normal·p <= Offset.
internal readonly record struct SolidPlane(Vector3 Normal, float Offset);

// A convex piece: its faces and its corners.
internal readonly record struct SolidHull(SolidPlane[] Planes, Vector3[] Vertices);

// A triangle mesh. Closed, Rubikon counts what it encloses as in it: a player anywhere inside bhop_lego3's mesh zones
// touches them. Open, only its surface touches.
internal readonly record struct SolidMesh(Vector3[] Vertices, int[] Triangles);

internal enum ZoneSolidity
{
    // Every column through it is covered from its bottom to its top.
    Solid,

    // Convex pieces with gaps between them: shells around an empty inside.
    Hollow,

    // A closed mesh, its inside as much in it as its surface.
    Mesh,

    // A mesh with open edges: only its surface touches.
    MeshOpen,

    // Under 16 units tall: a jump leaves it.
    Thin,
}

internal readonly record struct ZoneSolidityReport(ZoneSolidity Kind, Vector3 Min, Vector3 Max, int Columns, float HollowPercent,
                                                   float MaxHeight, float FootprintConvexPercent)
{
    // A player anywhere inside it touches it.
    public bool HoldsInside
        => Kind is ZoneSolidity.Solid or ZoneSolidity.Mesh;

    // Rebuilt as one convex hull around all its points it keeps its shape: shells around a convex footprint. An open
    // mesh or a thin plate stays as the map made it: there's no telling how tall it was meant to be.
    public bool CanRebuildConvex
        => Kind == ZoneSolidity.Hollow && FootprintConvexPercent >= 95f;
}

internal static class ZoneSolidityCheck
{
    // A gap this tall in a column could hold a player's middle while they touch nothing.
    private const float GapLimit = 8f;

    // Columns a side, at most.
    private const float Grid = 48f;

    // Cuts the shape with vertical lines on a grid over its footprint and looks at what each line crosses.
    public static ZoneSolidityReport Measure(IReadOnlyList<SolidHull> hulls, IReadOnlyList<SolidMesh> meshes)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var hull in hulls)
        {
            foreach (var v in hull.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        foreach (var mesh in meshes)
        {
            foreach (var v in mesh.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        if (min.X > max.X)
        {
            return new ZoneSolidityReport(ZoneSolidity.Solid, default, default, 0, 0, 0, 100);
        }

        var size = max - min;

        // Each axis on its own, so a wall 16 units thin still gets its columns.
        var stepX = MathF.Max(1f, size.X / Grid);
        var stepY = MathF.Max(1f, size.Y / Grid);

        var spans     = new List<(float Lo, float Hi)>();
        var covered   = new List<Vector2>();
        var hollow    = 0;
        var maxHeight = 0f;

        for (var x = min.X + (stepX / 2); x < max.X; x += stepX)
        {
            for (var y = min.Y + (stepY / 2); y < max.Y; y += stepY)
            {
                spans.Clear();

                foreach (var hull in hulls)
                {
                    if (Column(hull.Planes, x, y) is { } span)
                    {
                        spans.Add(span);
                    }
                }

                foreach (var mesh in meshes)
                {
                    var t = mesh.Triangles;

                    for (var i = 0; i + 2 < t.Length; i += 3)
                    {
                        if ((uint) t[i] < (uint) mesh.Vertices.Length
                            && (uint) t[i + 1] < (uint) mesh.Vertices.Length
                            && (uint) t[i + 2] < (uint) mesh.Vertices.Length
                            && Crossing(mesh.Vertices[t[i]], mesh.Vertices[t[i + 1]], mesh.Vertices[t[i + 2]], x, y) is { } z)
                        {
                            spans.Add((z, z));
                        }
                    }
                }

                if (spans.Count == 0)
                {
                    continue;
                }

                covered.Add(new Vector2(x, y));
                spans.Sort();

                var top = spans[0].Hi;
                var gap = false;

                foreach (var (lo, hi) in spans)
                {
                    gap |= lo - top >= GapLimit;
                    top =  MathF.Max(top, hi);
                }

                maxHeight = MathF.Max(maxHeight, top - spans[0].Lo);

                if (gap)
                {
                    hollow++;
                }
            }
        }

        var hollowPercent = covered.Count == 0 ? 0 : 100f * hollow / covered.Count;

        var convexPercent = covered.Count < 3
            ? 100f
            : MathF.Min(100f, 100f * covered.Count * stepX * stepY / MathF.Max(1f, FootprintHullArea(covered, stepX, stepY)));

        var kind = meshes.Count > 0 ? !meshes.All(IsClosed) ? ZoneSolidity.MeshOpen
                                      : maxHeight < 16      ? ZoneSolidity.Thin
                                                            : ZoneSolidity.Mesh
                   : hollowPercent >= 5 ? ZoneSolidity.Hollow
                   : maxHeight < 16     ? ZoneSolidity.Thin
                                        : ZoneSolidity.Solid;

        return new ZoneSolidityReport(kind, min, max, covered.Count, hollowPercent, maxHeight, convexPercent);
    }

    // Every edge shared by an even number of triangles, corners matched by where they are: a mesh may repeat a corner.
    private static bool IsClosed(SolidMesh mesh)
    {
        var corners = new Dictionary<Vector3, int>();
        var edges   = new Dictionary<(int, int), int>();
        var t       = mesh.Triangles;

        for (var i = 0; i + 2 < t.Length; i += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                if ((uint) t[i + k] >= (uint) mesh.Vertices.Length || (uint) t[i + ((k + 1) % 3)] >= (uint) mesh.Vertices.Length)
                {
                    return false;
                }

                var a = Corner(corners, mesh.Vertices[t[i + k]]);
                var b = Corner(corners, mesh.Vertices[t[i + ((k + 1) % 3)]]);

                if (a != b)
                {
                    var edge = a < b ? (a, b) : (b, a);
                    edges[edge] = edges.GetValueOrDefault(edge) + 1;
                }
            }
        }

        if (edges.Count == 0)
        {
            return false;
        }

        foreach (var count in edges.Values)
        {
            if (count % 2 != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Corner(Dictionary<Vector3, int> corners, Vector3 position)
    {
        if (!corners.TryGetValue(position, out var id))
        {
            id                = corners.Count;
            corners[position] = id;
        }

        return id;
    }

    // Where a vertical line through (x, y) is inside a convex piece.
    private static (float Lo, float Hi)? Column(SolidPlane[] planes, float x, float y)
    {
        float lo = float.MinValue, hi = float.MaxValue;

        foreach (var p in planes)
        {
            var rest = p.Offset - (p.Normal.X * x) - (p.Normal.Y * y);

            if (MathF.Abs(p.Normal.Z) < 1e-5f)
            {
                if (rest < -0.01f)
                {
                    return null;
                }

                continue;
            }

            var z = rest / p.Normal.Z;

            if (p.Normal.Z > 0)
            {
                hi = MathF.Min(hi, z);
            }
            else
            {
                lo = MathF.Max(lo, z);
            }
        }

        return lo <= hi ? (lo, hi) : null;
    }

    // Where a vertical line through (x, y) crosses the triangle, if it does.
    private static float? Crossing(Vector3 a, Vector3 b, Vector3 c, float x, float y)
    {
        var d = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));

        if (MathF.Abs(d) < 1e-6f)
        {
            return null;
        }

        var u = (((b.Y - c.Y) * (x - c.X)) + ((c.X - b.X) * (y - c.Y))) / d;
        var v = (((c.Y - a.Y) * (x - c.X)) + ((a.X - c.X) * (y - c.Y))) / d;
        var w = 1 - u - v;

        return u >= 0 && v >= 0 && w >= 0 ? (u * a.Z) + (v * b.Z) + (w * c.Z) : null;
    }

    // The area of the convex hull around the covered columns' cells.
    private static float FootprintHullArea(List<Vector2> centres, float stepX, float stepY)
    {
        var hx     = stepX / 2;
        var hy     = stepY / 2;
        var points = new List<Vector2>(centres.Count * 4);

        foreach (var c in centres)
        {
            points.Add(c + new Vector2(-hx, -hy));
            points.Add(c + new Vector2(hx, -hy));
            points.Add(c + new Vector2(hx, hy));
            points.Add(c + new Vector2(-hx, hy));
        }

        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));

        // Andrew's monotone chain, lower then upper.
        var hull = new List<Vector2>();

        for (var pass = 0; pass < 2; pass++)
        {
            var start = hull.Count;

            for (var i = 0; i < points.Count; i++)
            {
                var p = points[pass == 0 ? i : points.Count - 1 - i];

                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        var area = 0f;

        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return MathF.Abs(area) / 2;
    }

    private static float Cross(Vector2 o, Vector2 a, Vector2 b)
        => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
}
