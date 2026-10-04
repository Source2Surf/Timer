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

namespace Timer.MapChooser.Logic;

/// <summary>
///     The last maps played, newest first, the current one included. They can't be nominated or come up in a vote.
/// </summary>
internal sealed class RecentMaps
{
    private readonly List<string> _maps = [];

    public RecentMaps(int capacity, IEnumerable<string>? maps = null)
    {
        Capacity = capacity;

        foreach (var map in (maps ?? []).Reverse())
        {
            Push(map);
        }
    }

    public int Capacity { get; }

    public IReadOnlyList<string> Maps => _maps;

    public void Push(string map)
    {
        _maps.RemoveAll(x => x.Equals(map, StringComparison.OrdinalIgnoreCase));
        _maps.Insert(0, map);

        if (_maps.Count > Capacity)
        {
            _maps.RemoveRange(Capacity, _maps.Count - Capacity);
        }
    }

    public bool Contains(string map)
        => _maps.Any(x => x.Equals(map, StringComparison.OrdinalIgnoreCase));
}
