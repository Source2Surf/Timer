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
using Source2Surf.Timer.Modules.Hud;

namespace Source2Surf.Timer.Modules;

// !hide and the finish sounds: options in the player's settings, for the modules that act on them.
internal partial class HudModule
{
    public event Action<PlayerSlot>? Changed;

    public bool HidesPlayers(PlayerSlot slot)
        => _players[slot] is { } p ? p.IsOn(HudOptions.Hide) : HudOptions.Hide.Initial == 0;

    public bool PlaysSounds(PlayerSlot slot)
        => _players[slot] is { } p ? p.IsOn(HudOptions.Sounds) : HudOptions.Sounds.Initial == 0;

    public bool HearsFootsteps(PlayerSlot slot)
        => _players[slot] is { } p ? p.IsOn(HudOptions.Footsteps) : HudOptions.Footsteps.Initial == 0;

    public bool HearsWeaponSounds(PlayerSlot slot)
        => _players[slot] is { } p ? p.IsOn(HudOptions.WeaponSounds) : HudOptions.WeaponSounds.Initial == 0;

    public void SetHearsFootsteps(PlayerSlot slot, bool value)
        => SetOnOff(slot, HudOptions.Footsteps, value);

    public void SetHearsWeaponSounds(PlayerSlot slot, bool value)
        => SetOnOff(slot, HudOptions.WeaponSounds, value);

    public void SetHidesPlayers(PlayerSlot slot, bool value)
        => SetOnOff(slot, HudOptions.Hide, value);

    public void SetPlaysSounds(PlayerSlot slot, bool value)
        => SetOnOff(slot, HudOptions.Sounds, value);

    // Like a click in the menu: saved after the usual delay.
    private void SetOnOff(PlayerSlot slot, HudOption option, bool on)
    {
        if (_players[slot] is not { } p || p.IsOn(option) == on)
        {
            return;
        }

        p.Settings[option.Index] = on ? 0 : 1;
        p.MenuDirty              = true;
        MarkSettingsChanged(p);
    }
}
