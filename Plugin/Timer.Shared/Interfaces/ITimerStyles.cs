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
using Source2Surf.Timer.Shared.Models.Style;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
///     The timer's styles, for other modules. Main thread only.
/// </summary>
public interface ITimerStyles
{
    static readonly string Identity = typeof(ITimerStyles).FullName!;

    /// <summary>
    ///     The style the player's timer is on, or null when they have none. A module's own keys in
    ///     timer-styles.jsonc are in its <see cref="StyleSetting.ExtensionData" />.
    /// </summary>
    StyleSetting? GetPlayerStyle(PlayerSlot slot);
}
