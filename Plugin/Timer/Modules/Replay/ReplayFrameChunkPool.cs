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
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
///     Spare chunks for <see cref="ReplayFrameBuffer" />, shared by every recording and snapshot: a chunk is allocated
///     once and passed around after that. Past its limit a returned chunk is left to the GC. Thread-safe, since a
///     snapshot can be read into chunks off the game thread.
/// </summary>
internal sealed class ReplayFrameChunkPool
{
    /// <summary>
    ///     64 MB of spares, what 64 players each holding a four-minute run would give back.
    /// </summary>
    public const int DefaultMaxSpare = 1024;

    public static ReplayFrameChunkPool Shared { get; } = new (DefaultMaxSpare);

    private readonly Stack<ReplayFrameData[]> _spare = new ();
    private readonly object                   _gate  = new ();
    private readonly int                      _maxSpare;

    public ReplayFrameChunkPool(int maxSpare)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxSpare);

        _maxSpare = maxSpare;
    }

    public int SpareCount
    {
        get
        {
            lock (_gate)
            {
                return _spare.Count;
            }
        }
    }

    public ReplayFrameData[] Rent()
    {
        lock (_gate)
        {
            if (_spare.TryPop(out var chunk))
            {
                return chunk;
            }
        }

        // Kept for as long as the plugin runs, so it goes where the GC never copies it: on the small object heap a
        // chunk would be copied twice on its way to gen2. Uninitialized, since only frames written are ever read.
        return GC.AllocateUninitializedArray<ReplayFrameData>(ReplayFrameBuffer.ChunkFrames, pinned: true);
    }

    public void Return(ReadOnlySpan<ReplayFrameData[]> chunks)
    {
        lock (_gate)
        {
            foreach (var chunk in chunks)
            {
                if (_spare.Count >= _maxSpare)
                {
                    return;
                }

                _spare.Push(chunk);
            }
        }
    }
}
