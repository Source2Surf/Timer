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

using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules.Hud;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// !fov <n>: the player's field of view, a player setting. Applied when the settings load or change and on every
// spawn; a player who never set one keeps whatever the game gives them.
internal partial class MiscModule
{
    // By player slot: whether !fov changed their FOV, so going back to 0 restores the default.
    private ulong _fovApplied;

    public void OnClientPutInServer(PlayerSlot slot)
        => ApplyFov(slot);

    public void OnClientDisconnected(PlayerSlot slot)
        => _fovApplied &= ~(1UL << slot);

    private void ApplyFov(PlayerSlot slot)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return;
        }

        var bit = 1UL << slot;
        var fov = _settings.Fov(slot);

        if (fov == 0)
        {
            if ((_fovApplied & bit) == 0)
            {
                return;
            }

            _fovApplied &= ~bit;
            fov         =  HudOptions.DefaultFov;
        }
        else
        {
            _fovApplied |= bit;
        }

        if (controller.DesiredFOV != fov)
        {
            controller.DesiredFOV = (uint) fov;
        }
    }

    // !fov <n> sets it, !fov alone (or !fov 0) goes back to the default.
    private ECommandAction OnCommandFov(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr  = _localization.For(slot);
        var arg = command.ArgString.Trim();

        if (arg.Length == 0 || arg == "0")
        {
            _settings.SetFov(slot, 0);
            ApplyFov(slot);
            controller.PrintToChat(tr[ChatTexts.FovReset]);

            return ECommandAction.Handled;
        }

        if (!int.TryParse(arg, out var fov) || fov is < HudOptions.MinFov or > HudOptions.MaxFov)
        {
            controller.PrintToChat(tr.Format(ChatTexts.FovUsage, HudOptions.MinFov, HudOptions.MaxFov));

            return ECommandAction.Handled;
        }

        _settings.SetFov(slot, fov);
        ApplyFov(slot);
        controller.PrintToChat(tr.Format(ChatTexts.FovSet, fov));

        return ECommandAction.Handled;
    }
}
