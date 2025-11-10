/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
using System.Runtime.CompilerServices;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;

namespace SurfTimer.Modules.Zone;

internal class BuildZoneInfo
{
    public Vector[] Points { get; set; } = new Vector[2];

    public IBaseModelEntity   DirectionBeam { get; set; } = null!;
    public IBaseModelEntity[] SnapBeams     { get; set; } = new IBaseModelEntity[2];

    public IBaseModelEntity?[] RenderBeams { get; set; } = new IBaseModelEntity?[12];

    public int       Step  { get; set; }  = 0;
    public int       Track { get; init; } = 0;
    public EZoneType Zone  { get; init; } = EZoneType.Invalid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void KillBeams()
    {
        DirectionBeam?.Kill();

        foreach (var aimPosBeam in SnapBeams)
        {
            aimPosBeam?.Kill();
        }

        foreach (var renderBeam in RenderBeams)
        {
            renderBeam?.Kill();
        }
    }

    public void RenderPreviewBeams(in Vector p1, in Vector p2)
    {
        if (RenderBeams[0] == null)
        {
            return;
        }

        Span<Vector> points =
        [
            p1,                     // back,  left,  bottom
            new (p1.X, p2.Y, p1.Z), // back,  right, bottom
            new (p2.X, p2.Y, p1.Z), // front, right, bottom
            new (p2.X, p1.Y, p1.Z), // front, left,  bottom

            new (p1.X, p1.Y, p2.Z), // back,  left,  top
            new (p1.X, p2.Y, p2.Z), // back,  right, top
            p2,                     // front, right, top
            new (p2.X, p1.Y, p2.Z), // front, left,  top
        ];

        Span<(int first, int second)> edges =
        [
            // bottom edges
            (0, 1),
            (1, 2),
            (2, 3),
            (3, 0),

            // top edges
            (4, 5),
            (5, 6),
            (6, 7),
            (7, 4),

            // vertical edges
            (0, 4),
            (1, 5),
            (2, 6),
            (3, 7),
        ];

        var renderBeams = RenderBeams;

        for (var i = 0; i < edges.Length; i++)
        {
            var (first, second) = edges[i];

            if (i < renderBeams.Length)
            {
                renderBeams[i]!.SetAbsOrigin(points[first]);
                renderBeams[i]!.SetNetVar("m_vecEndPos", points[second]);
            }
        }
    }
}