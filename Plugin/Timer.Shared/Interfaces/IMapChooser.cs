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

using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
///     The map chooser's state, for a HUD to show, and what its menus can do. Main thread only.
/// </summary>
public interface IMapChooser
{
    static readonly string Identity = typeof(IMapChooser).FullName!;

    /// <summary>
    ///     Changes whenever anything below does, so a HUD only redraws then.
    /// </summary>
    int Version { get; }

    /// <summary>
    ///     Changes with <see cref="Version" /> and with the player's own cursor and nominate menu: what their HUD
    ///     redraws on, so one player's keys don't redraw everyone's.
    /// </summary>
    int VersionFor(PlayerSlot slot)
        => Version;

    /// <summary>
    ///     The running vote, or null.
    /// </summary>
    IMapVote? Vote { get; }

    /// <summary>
    ///     The option the player voted for, or -1.
    /// </summary>
    int GetVoteChoice(PlayerSlot slot);

    /// <summary>
    ///     The option the player's keys are on.
    /// </summary>
    int GetVoteCursor(PlayerSlot slot);

    void CastVote(PlayerSlot slot, int option);

    MapVoteKeys VoteKeys { get; }

    /// <summary>
    ///     The map chosen to come next, or null.
    /// </summary>
    string? NextMap { get; }

    /// <summary>
    ///     Seconds until the map ends.
    /// </summary>
    float TimeLeft { get; }

    /// <summary>
    ///     The nominate menu the player opened, or null.
    /// </summary>
    NominateMenu? GetNominateMenu(PlayerSlot slot);

    void SetNominateFilter(PlayerSlot slot, int tier, bool unfinishedOnly);

    void CloseNominateMenu(PlayerSlot slot);

    NominateResult Nominate(PlayerSlot slot, string map);
}
