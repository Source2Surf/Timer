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

namespace Source2Surf.Timer.Shared.Models;

public enum MapVoteKind
{
    EndOfMap,
    RockTheVote,
}

/// <summary>
///     A map to vote for, or with <see cref="ExtendMinutes" /> set, staying on this one that much longer.
/// </summary>
public sealed record MapVoteOption(string Map, byte Tier, int ExtendMinutes = 0)
{
    public bool IsExtend => ExtendMinutes > 0;
}

/// <summary>
///     A running vote. Its counts change as players vote.
/// </summary>
public interface IMapVote
{
    MapVoteKind Kind { get; }

    IReadOnlyList<MapVoteOption> Options { get; }

    IReadOnlyList<int> Counts { get; }

    /// <summary>
    ///     When it closes, in game time (CurTime).
    /// </summary>
    float EndsAt { get; }
}

/// <summary>
///     The client commands whose keys move along a vote's options and cast the vote, e.g. "autobuy", "rebuy" and
///     "lookatweapon", for showing the player's own keys.
/// </summary>
public sealed record MapVoteKeys(string Up, string Down, string Select);

public enum NominateResult
{
    Nominated,
    Replaced,
    AlreadyNominated,
    Full,
    CurrentMap,
    Recent,
    NotFound,
    Closed,
}

public enum NominateState
{
    Available,
    Mine,
    Nominated,
    Recent,
    Current,
}

/// <param name="PersonalBest">The player's best time on it, or null when they haven't finished it (or it's loading).</param>
public sealed record NominateEntry(string Map, byte Tier, float? PersonalBest, NominateState State);

/// <summary>
///     The nominate menu a player has open: the maps matching their filter, in name order.
/// </summary>
/// <param name="Tier">The tier shown, or 0 for all.</param>
public sealed record NominateMenu(string? Search, int Tier, bool UnfinishedOnly, IReadOnlyList<NominateEntry> Entries);
