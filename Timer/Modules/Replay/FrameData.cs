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
 
using System.Collections.Generic;
using MemoryPack;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;

namespace SurfTimer.Modules.Replay;

internal record ReplayFileHeader
{
    public int Version { get; init; } = 1;

    public ulong SteamId     { get; init; }
    public int   TotalFrames { get; init; }
    public int   PreFrame    { get; init; }
    public int   PostFrame   { get; init; }
    public float Time        { get; init; }

    public string PlayerName { get; init; } = string.Empty;

    public List<int>? StageTicks { get; init; }
}

internal record ReplayContent
{
    public required ReplayFileHeader  Header { get; init; }
    public required ReplayFrameData[] Frames { get; init; }
}

[MemoryPackable]
internal readonly partial record struct ReplayFrameData
{
    [MemoryPackOrder(0)]
    public Vector Origin { get; init; }

    [MemoryPackOrder(1)]
    public Vector2D Angles { get; init; }

    [MemoryPackOrder(2)]
    public UserCommandButtons PressedButtons { get; init; }

    [MemoryPackOrder(3)]
    public UserCommandButtons ChangedButtons { get; init; }

    [MemoryPackOrder(4)]
    public UserCommandButtons ScrollButtons { get; init; }

    [MemoryPackOrder(5)]
    public MoveType MoveType { get; init; }

    [MemoryPackOrder(6)]
    public Vector Velocity { get; init; }
}
