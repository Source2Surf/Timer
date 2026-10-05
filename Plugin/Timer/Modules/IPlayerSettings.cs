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
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Modules;

/// <summary>
///     The player settings other modules act on, saved with the rest of the player's settings. They read as defaults
///     until the settings load after joining; <see cref="Changed" /> fires on the game thread when they load or change.
/// </summary>
internal interface IPlayerSettings
{
    /// <summary>
    ///     !hide: other players and replay bots aren't sent to this player. Off by default.
    /// </summary>
    bool HidesPlayers(PlayerSlot slot);

    void SetHidesPlayers(PlayerSlot slot, bool value);

    /// <summary>
    ///     The finish sounds. On by default.
    /// </summary>
    bool PlaysSounds(PlayerSlot slot);

    void SetPlaysSounds(PlayerSlot slot, bool value);

    event Action<PlayerSlot>? Changed;
}
