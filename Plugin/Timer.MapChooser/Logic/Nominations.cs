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
using Source2Surf.Timer.Shared.Models;

namespace Timer.MapChooser.Logic;

/// <summary>
///     One map per player, in the order they were nominated, which is the order they go into the vote.
/// </summary>
internal sealed class Nominations
{
    private readonly List<(int Slot, string Map)> _list = [];

    public int Max { get; set; } = 5;

    public int Count => _list.Count;

    public IEnumerable<string> Maps => _list.Select(x => x.Map);

    public NominateResult Add(int slot, string map)
    {
        if (_list.Any(x => x.Map.Equals(map, StringComparison.OrdinalIgnoreCase)))
        {
            return NominateResult.AlreadyNominated;
        }

        var mine = _list.FindIndex(x => x.Slot == slot);

        if (mine >= 0)
        {
            _list[mine] = (slot, map);

            return NominateResult.Replaced;
        }

        if (_list.Count >= Max)
        {
            return NominateResult.Full;
        }

        _list.Add((slot, map));

        return NominateResult.Nominated;
    }

    public string? Of(int slot)
        => _list.FirstOrDefault(x => x.Slot == slot).Map;

    public bool Contains(string map)
        => _list.Any(x => x.Map.Equals(map, StringComparison.OrdinalIgnoreCase));

    public string? Remove(int slot)
    {
        var mine = _list.FindIndex(x => x.Slot == slot);

        if (mine < 0)
        {
            return null;
        }

        var map = _list[mine].Map;
        _list.RemoveAt(mine);

        return map;
    }

    public void Clear()
        => _list.Clear();
}
