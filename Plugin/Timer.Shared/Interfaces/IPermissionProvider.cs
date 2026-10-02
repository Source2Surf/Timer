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

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
/// Decides what players may do beyond their own runs, such as controlling someone else's replay.
/// Register an implementation under <see cref="Identity" /> to connect the Timer to an admin system;
/// without one, no player has any of these permissions.
/// </summary>
public interface IPermissionProvider
{
    static readonly string Identity = typeof(IPermissionProvider).FullName!;

    /// <summary>
    /// Control a central replay bot that another player started: seek, pause, speed and stop.
    /// </summary>
    const string ReplayControl = "timer:replay";

    /// <summary>
    /// Whether the player has <paramref name="permission" />, one of the constants on this interface.
    /// Called on the game thread, as often as the HUD refreshes, so it should be a cheap lookup.
    /// </summary>
    bool HasPermission(SteamID steamId, string permission);
}
