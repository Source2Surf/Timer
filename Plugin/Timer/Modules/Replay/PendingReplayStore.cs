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

using System.Collections.Generic;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
/// Manages pending replays awaiting record save results.
/// All operations run on the game main thread — no locking needed.
/// </summary>
internal sealed class PendingReplayStore
{
    private readonly Dictionary<ReplayMatchKey, PendingReplay> _pending = [];

    /// <summary>
    /// Store a pending replay. Returns the replaced entry (if any) for fallback-save.
    /// </summary>
    public PendingReplay? Add(ReplayMatchKey key, PendingReplay replay)
    {
        _pending.Remove(key, out var replaced);
        _pending.Add(key, replay);
        return replaced;
    }

    /// <summary>
    /// Remove and return a pending replay by key, or null if not found.
    /// </summary>
    public PendingReplay? TakeMatch(ReplayMatchKey key)
    {
        if (_pending.Remove(key, out var entry))
            return entry;

        return null;
    }

    /// <summary>
    /// Remove and return all pending replays, clearing the store.
    /// </summary>
    public List<KeyValuePair<ReplayMatchKey, PendingReplay>> TakeAll()
    {
        var all = new List<KeyValuePair<ReplayMatchKey, PendingReplay>>(_pending);
        _pending.Clear();
        return all;
    }

    public int Count => _pending.Count;
}
