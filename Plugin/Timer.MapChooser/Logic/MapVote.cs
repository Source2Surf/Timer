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

internal sealed class MapVote : IMapVote
{
    private readonly Dictionary<int, int> _choices = [];
    private readonly int[]                _counts;

    public MapVote(MapVoteKind kind, IReadOnlyList<MapVoteOption> options, float endsAt)
    {
        Kind    = kind;
        Options = options;
        EndsAt  = endsAt;
        _counts = new int[options.Count];
    }

    public MapVoteKind                  Kind    { get; }
    public IReadOnlyList<MapVoteOption> Options { get; }
    public IReadOnlyList<int>           Counts  => _counts;
    public float                        EndsAt  { get; }

    public int Voters => _choices.Count;

    /// <summary>
    ///     Votes, or changes the player's vote. False when the option doesn't exist or is already theirs.
    /// </summary>
    public bool Cast(int slot, int option)
    {
        if (option < 0 || option >= _counts.Length || Choice(slot) == option)
        {
            return false;
        }

        Remove(slot);
        _choices[slot] = option;
        _counts[option]++;

        return true;
    }

    public bool Remove(int slot)
    {
        if (!_choices.Remove(slot, out var option))
        {
            return false;
        }

        _counts[option]--;

        return true;
    }

    public int Choice(int slot)
        => _choices.GetValueOrDefault(slot, -1);

    /// <summary>
    ///     The option with the most votes, a tie drawn at random. With no votes at all, a random map.
    /// </summary>
    public int Winner(Random random)
    {
        var most = _counts.Length == 0 ? 0 : _counts.Max();

        var leaders = Enumerable.Range(0, _counts.Length)
                                .Where(i => most > 0 ? _counts[i] == most : !Options[i].IsExtend)
                                .ToList();

        return leaders.Count == 0 ? -1 : leaders[random.Next(leaders.Count)];
    }
}
