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

namespace Timer.MapChooser.Logic;

internal sealed class RockTheVote
{
    private readonly HashSet<int> _voters = [];

    public int Count => _voters.Count;

    public bool Add(int slot)
        => _voters.Add(slot);

    public bool Remove(int slot)
        => _voters.Remove(slot);

    public bool Has(int slot)
        => _voters.Contains(slot);

    public void Clear()
        => _voters.Clear();

    /// <summary>
    ///     Votes needed out of this many players: the share, rounded up, and never fewer than one.
    /// </summary>
    public static int Needed(int players, float ratio)
        => Math.Max(1, (int) MathF.Ceiling((players * ratio) - 0.0001f));
}
