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
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
///     Replay frames in chunks of <see cref="ChunkFrames" /> (64 KB, below the large object heap) from a
///     <see cref="ReplayFrameChunkPool" />. Adding never copies what's there, dropping frames gives back the chunks it
///     empties, and a finished run hands its chunks to its snapshot (<see cref="Detach" />) instead of copying them.
///     Not thread-safe: one owner at a time.
/// </summary>
internal sealed class ReplayFrameBuffer : IReadOnlyList<ReplayFrameData>
{
    private const int ChunkShift = 10;

    /// <summary>
    ///     Frames per chunk: 1024 frames of 64 bytes.
    /// </summary>
    public const int ChunkFrames = 1 << ChunkShift;

    private const int ChunkMask = ChunkFrames - 1;

    private readonly ReplayFrameChunkPool    _pool;
    private readonly List<ReplayFrameData[]> _chunks;

    // Where frame 0 is in the first chunk.
    private int _start;

    public ReplayFrameBuffer()
        : this(ReplayFrameChunkPool.Shared)
    {
    }

    public ReplayFrameBuffer(ReplayFrameChunkPool pool)
        : this(pool, [], 0, 0)
    {
    }

    private ReplayFrameBuffer(ReplayFrameChunkPool pool, List<ReplayFrameData[]> chunks, int start, int count)
    {
        _pool   = pool;
        _chunks = chunks;
        _start  = start;
        Count   = count;
    }

    public int Count { get; private set; }

    /// <summary>
    ///     The chunks held: no more than the frames need.
    /// </summary>
    public int ChunkCount => _chunks.Count;

    public ReplayFrameData this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Frame index out of range [0, {Count}).");
            }

            var i = _start + index;

            return _chunks[i >> ChunkShift][i & ChunkMask];
        }
    }

    public void Add(ReplayFrameData frame)
    {
        var i     = _start + Count;
        var chunk = i >> ChunkShift;

        if (chunk == _chunks.Count)
        {
            _chunks.Add(_pool.Rent());
        }

        _chunks[chunk][i & ChunkMask] = frame;
        Count++;
    }

    public void AddRange(ReadOnlySpan<ReplayFrameData> frames)
    {
        while (!frames.IsEmpty)
        {
            var i     = _start + Count;
            var chunk = i >> ChunkShift;

            if (chunk == _chunks.Count)
            {
                _chunks.Add(_pool.Rent());
            }

            var offset = i & ChunkMask;
            var length = Math.Min(ChunkFrames - offset, frames.Length);

            frames[..length].CopyTo(_chunks[chunk].AsSpan(offset, length));
            frames =  frames[length..];
            Count  += length;
        }
    }

    /// <summary>
    ///     A copy of <paramref name="length" /> frames from <paramref name="start" /> on, in chunks of its own: for part of
    ///     a recording that goes on.
    /// </summary>
    public ReplayFrameBuffer CopyRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Count - start);

        var copy = new ReplayFrameBuffer(_pool);
        AppendRangeTo(copy, start, length);

        return copy;
    }

    /// <summary>
    ///     Hands every frame to a new buffer and keeps only copies of the last <paramref name="keep" />: a finished run's
    ///     frames go to its snapshot without a copy, and their end stays here as the next run's pre-run.
    /// </summary>
    public ReplayFrameBuffer Detach(int keep)
    {
        var detached = new ReplayFrameBuffer(_pool, [.. _chunks], _start, Count);

        _chunks.Clear();
        _start = 0;
        Count  = 0;

        var kept = Math.Clamp(keep, 0, detached.Count);
        detached.AppendRangeTo(this, detached.Count - kept, kept);

        return detached;
    }

    /// <summary>
    ///     Drops the first <paramref name="count" /> frames, the way <see cref="List{T}.RemoveRange" /> from 0 would.
    /// </summary>
    public void RemoveFirst(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);

        if (count == Count)
        {
            Clear();

            return;
        }

        _start += count;
        Count  -= count;

        var emptied = _start >> ChunkShift;

        if (emptied > 0)
        {
            _pool.Return(CollectionsMarshal.AsSpan(_chunks)[..emptied]);
            _chunks.RemoveRange(0, emptied);
            _start &= ChunkMask;
        }
    }

    /// <summary>
    ///     Drops every frame from <paramref name="count" /> on.
    /// </summary>
    public void Truncate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Count);

        if (count == 0)
        {
            Clear();

            return;
        }

        Count = count;

        var used = ((_start + count - 1) >> ChunkShift) + 1;

        if (used < _chunks.Count)
        {
            _pool.Return(CollectionsMarshal.AsSpan(_chunks)[used..]);
            _chunks.RemoveRange(used, _chunks.Count - used);
        }
    }

    /// <summary>
    ///     Drops every frame and gives back every chunk. Whoever still holds this buffer sees it empty, never a chunk
    ///     that went on to be reused.
    /// </summary>
    public void Clear()
    {
        _pool.Return(CollectionsMarshal.AsSpan(_chunks));
        _chunks.Clear();
        _start = 0;
        Count  = 0;
    }

    private void AppendRangeTo(ReplayFrameBuffer target, int start, int length)
    {
        var i = _start + start;

        while (length > 0)
        {
            var offset = i & ChunkMask;
            var count  = Math.Min(ChunkFrames - offset, length);

            target.AddRange(_chunks[i >> ChunkShift].AsSpan(offset, count));
            i      += count;
            length -= count;
        }
    }

    public Enumerator GetEnumerator()
        => new (this);

    IEnumerator<ReplayFrameData> IEnumerable<ReplayFrameData>.GetEnumerator()
        => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();

    public struct Enumerator : IEnumerator<ReplayFrameData>
    {
        private readonly ReplayFrameBuffer _buffer;
        private          int               _index;

        internal Enumerator(ReplayFrameBuffer buffer)
        {
            _buffer = buffer;
            _index  = -1;
        }

        public readonly ReplayFrameData Current => _buffer[_index];

        readonly object IEnumerator.Current => Current;

        public bool MoveNext()
            => ++_index < _buffer.Count;

        public void Reset()
            => _index = -1;

        public readonly void Dispose()
        {
        }
    }
}
