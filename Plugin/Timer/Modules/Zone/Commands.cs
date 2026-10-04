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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Zone;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class ZoneModule
{
    // !zone opens the zone editor; !zone cancel; !zone <type> [number]; !zone b<track> <type> [number].
    private ECommandAction OnCommandZone(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true })
        {
            return ECommandAction.Handled;
        }

        _zoneEditors[slot] = true;

        if (command.ArgCount < 1)
        {
            EditorRequested?.Invoke(slot);

            return ECommandAction.Handled;
        }

        if (command.GetArg(1).Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            CancelZoneBuild(slot);

            return ECommandAction.Handled;
        }

        var args = new string[command.ArgCount];

        for (var i = 0; i < args.Length; i++)
        {
            args[i] = command.GetArg(i + 1);
        }

        if (!ZoneEdit.TryParse(args, out var track, out var type, out var number))
        {
            _logger.LogInformation("Invalid zone arguments: {Args}", string.Join(' ', args));

            return ECommandAction.Handled;
        }

        StartZoneBuild(slot, track, type, number ?? NextZoneNumber(track, type));

        return ECommandAction.Handled;
    }
}