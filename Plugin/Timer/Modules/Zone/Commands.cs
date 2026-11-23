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
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared.Models.Zone;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class ZoneModule
{
    // !zone opens the zone editor; !zone cancel; !zone all; !zone <type> [number]; !zone b<track> <type> [number];
    // !zone edit, move <x> <y> <z>, rotate <degrees> and done for a zone added in game.
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

        // Every track's outlines while editing, or only the player's again.
        if (command.GetArg(1).Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return ToggleAllTrackLines(slot);
        }

        if (ZoneEdit.EditVerb(command.GetArg(1)) is { } verb)
        {
            return OnCommandZoneEdit(slot, verb, command);
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

    private ECommandAction OnCommandZoneEdit(PlayerSlot slot, string verb, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr = _localization.For(slot);

        if (verb == "done")
        {
            DeselectZone(slot);
            controller.PrintToChat(tr[ChatTexts.ZoneEditDone]);

            return ECommandAction.Handled;
        }

        // The zone picked to edit, else the one the player stands in.
        var id = verb == "edit" ? ZoneAt(slot) : GetEditTarget(slot) is var picked and not 0 ? picked : ZoneAt(slot);

        if (id == 0 || !_zones.TryGetValue(id, out var info))
        {
            controller.PrintToChat(tr[ChatTexts.ZoneEditNone]);

            return ECommandAction.Handled;
        }

        var name = ZoneChatName(tr, info);
        var args = new string[command.ArgCount - 1];

        for (var i = 0; i < args.Length; i++)
        {
            args[i] = command.GetArg(i + 2);
        }

        bool done;

        switch (verb)
        {
            case "edit":
                done = SelectZone(slot, id);

                if (done)
                {
                    controller.PrintToChat(tr.Format(ChatTexts.ZoneEditPicked, name, tr.Track(info.Track)));

                    return ECommandAction.Handled;
                }

                break;
            case "move" when ZoneEdit.TryParseMove(args, out var delta):
                done = MoveZone(slot, id, delta);

                break;
            case "rotate" or "resize" when !_canTurn:
                controller.PrintToChat(tr[ChatTexts.ZoneCantTurn]);

                return ECommandAction.Handled;
            case "rotate" when ZoneEdit.TryParseTurn(args, out var degrees):
                done = TurnZone(slot, id, degrees);

                break;
            case "resize" when ZoneEdit.TryParseResize(args, out var point, out var axis, out var by):
                done = ResizeZone(slot, id, point, axis, by);

                break;
            default:
                controller.PrintToChat(tr[ChatTexts.ZoneEditUsage]);

                return ECommandAction.Handled;
        }

        if (!done)
        {
            controller.PrintToChat(tr.Format(ChatTexts.ZoneEditFailed, name));

            return ECommandAction.Handled;
        }

        var center = (info.Corner1 + info.Corner2) / 2f;

        controller.PrintToChat(tr.Format(ChatTexts.ZoneEditPlaced,
                                         name,
                                         tr.Track(info.Track),
                                         ZoneEdit.FormatPoint(center),
                                         ZoneEdit.FormatYaw(YawOf(info))));

        return ECommandAction.Handled;
    }

    private static string ZoneChatName(ChatTr tr, ZoneInfo info)
        => info.ZoneType switch
        {
            EZoneType.Start      => tr[ChatTexts.ZoneStart],
            EZoneType.End        => tr[ChatTexts.ZoneEnd],
            EZoneType.Stage      => tr.Format(ChatTexts.TrackStage, info.Data),
            EZoneType.Checkpoint => tr.Format(ChatTexts.ZoneCheckpoint, info.Data),
            EZoneType.Reset      => tr[ChatTexts.ZoneReset],
            _                    => tr[ChatTexts.ZoneStopTimer],
        };
}