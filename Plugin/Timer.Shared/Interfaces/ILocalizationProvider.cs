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
/// Translates the Timer's player-facing texts. Register an implementation under <see cref="Identity" /> (the
/// Timer.Localization module connects ModSharp's LocalizerManager); without one, players read the Timer's English.
/// </summary>
public interface ILocalizationProvider
{
    static readonly string Identity = typeof(ILocalizationProvider).FullName!;

    /// <summary>
    /// The player's text for <paramref name="key" /> (keys such as "hud.title.map_completed"): a template with
    /// {0}, {1}… where the Timer puts its values, in the order the English one has them. Null when there's none in
    /// their language, and the Timer uses its English.
    /// <para>Asked on the game thread as often as the HUD refreshes, so it should be a cached lookup.</para>
    /// </summary>
    string? GetText(PlayerSlot slot, string key);

    /// <summary>
    /// Which language the player reads (equal for players reading the same one, such as its culture name); null when
    /// unknown. A message for everyone is then made once per language.
    /// </summary>
    object? LocaleOf(PlayerSlot slot)
        => null;
}
