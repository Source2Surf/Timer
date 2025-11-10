/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using SurfTimer.Managers.Player;
using SurfTimer.Modules.Zone;

// ReSharper disable once CheckNamespace
namespace SurfTimer.Modules;

internal partial class ZoneModule
{
    private ECommandAction OnCommandZone(IGamePlayer player, StringCommand command)
    {
        if (player.Controller is not { IsValidEntity: true } controller
            || controller.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        if (command.ArgCount < 1)
        {
            return ECommandAction.Handled;
        }

        var zoneOrType = command.GetArg(1);

        if (string.IsNullOrWhiteSpace(zoneOrType))
        {
            return ECommandAction.Handled;
        }

        var track = 0;

        var zoneOrTypeSpan = zoneOrType.AsSpan();

        if (!Enum.TryParse<EZoneType>(zoneOrTypeSpan, true, out var type))
        {
            if (zoneOrTypeSpan[0] != 'b')
            {
                _logger.LogInformation("Invalid type");

                return ECommandAction.Handled;
            }

            if (!int.TryParse(zoneOrTypeSpan[1..], out track) || track >= Utils.MAX_TRACK)
            {
                return ECommandAction.Handled;
            }
        }

        if (track > 0)
        {
            if (command.ArgCount < 2)
            {
                return ECommandAction.Handled;
            }

            var zone = command.GetArg(2);

            if (!Enum.TryParse(zone, true, out type))
            {
                return ECommandAction.Handled;
            }
        }

        var buildInfo = new BuildZoneInfo
        {
            Step  = 0,
            Track = track,
            Zone  = type,
        };

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", "255 255 255" },
            { "BoltWidth", "6" },
        };

        if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is { IsValidEntity: true } directionBeam)
        {
            buildInfo.DirectionBeam = directionBeam;
        }

        kv["rendercolor"] = "255 0 0";

        if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is { IsValidEntity: true } snapBeam1)
        {
            buildInfo.SnapBeams[0] = snapBeam1;
        }

        if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is { IsValidEntity: true } snapBeam2)
        {
            buildInfo.SnapBeams[1] = snapBeam2;
        }

        BuildZoneInfo[player.Slot] = buildInfo;

        return ECommandAction.Handled;
    }
}