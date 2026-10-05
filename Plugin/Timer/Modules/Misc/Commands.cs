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
using System.Collections.Generic;
using Cysharp.Text;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

internal unsafe partial class MiscModule
{
    private void AddCommands()
    {
        _commandManager.AddClientChatCommand("usp",   OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("glock", OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("knife", OnCommandGiveWeapon);
        _commandManager.AddClientChatCommand("spec",  OnCommandSpec);
        _commandManager.AddClientChatCommand("specs", OnCommandSpecs);
    }

    private ECommandAction OnCommandGiveWeapon(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.EntityManager.FindPlayerPawnBySlot(slot) is not { } basePawn
            || basePawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return ECommandAction.Handled;
        }

        var weapon = command.CommandName switch
        {
            "glock" or "usp" => pawn.GetWeaponBySlot(GearSlot.Pistol),
            "knife"          => pawn.GetWeaponBySlot(GearSlot.Knife),
            _                => null,
        };

        if (weapon is not null)
        {
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                if (pawn is { IsValidEntity: true } && weapon is { IsValidEntity: true })
                {
                    pawn.RemovePlayerItem(weapon);
                }
            });
        }

        if (command.CommandName.Equals("knife", StringComparison.OrdinalIgnoreCase))
        {
            pawn.GiveNamedItem("weapon_knife");
        }
        else
        {
            pawn.GiveNamedItem(command.CommandName.Equals("glock", StringComparison.OrdinalIgnoreCase)
                                   ? EconItemId.Glock
                                   : EconItemId.UspSilencer);
        }

        return ECommandAction.Handled;
    }

    // !spec watches the replay bot, !spec <name> that player.
    private ECommandAction OnCommandSpec(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var name   = command.ArgString.Trim();
        var target = default(PlayerSlot?);

        if (name.Length > 0)
        {
            var tr = _localization.For(slot);

            if (FindPlayer(slot, name, out var found) is { } problem)
            {
                controller.PrintToChat(tr.Format(problem, name));

                return ECommandAction.Handled;
            }

            if (_bridge.EntityManager.FindPlayerPawnBySlot(found)?.AsPlayer() is not { IsAlive: true })
            {
                controller.PrintToChat(tr.Format(ChatTexts.SpecUnavailable, Utils.Highlight(_bridge.ClientManager.GetGameClient(found)?.Name ?? name)));

                return ECommandAction.Handled;
            }

            target = found;
        }
        else if (_replayModule.GetReplayBotByIndex(0) is { } replayBot)
        {
            target = replayBot.Slot;
        }

        if (controller.GetPlayerPawn() is { IsAlive: true } pawn)
        {
            pawn.ChangeTeam(CStrikeTeam.Spectator);
        }

        if (target is not { } targetSlot)
        {
            return ECommandAction.Handled;
        }

        // After a team change the observer pawn only exists from the next frame.
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            if (_bridge.EntityManager.FindPlayerControllerBySlot(slot)?.GetObserverPawn()?.GetObserverService() is not { } observer
                || _bridge.EntityManager.FindPlayerPawnBySlot(targetSlot)?.AsPlayerPawn() is not { IsAlive: true } targetPawn)
            {
                return;
            }

            observer.ObserverMode   = observer.ObserverLastMode = ObserverMode.InEye;
            observer.ObserverTarget = targetPawn.Handle;
        });

        return ECommandAction.Handled;
    }

    // Who watches you, or the player you watch.
    private ECommandAction OnCommandSpecs(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var target     = _bridge.GetObservedSlot(controller) ?? slot;
        var targetName = Utils.Highlight(_bridge.ClientManager.GetGameClient(target)?.Name ?? "?");
        var names      = new List<string>();

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.Slot != target
                && !client.IsFakeClient
                && !client.IsHltv
                && client.GetPlayerController() is { } spectator
                && _bridge.GetObservedSlot(spectator) == target)
            {
                names.Add(client.Name);
            }
        }

        var tr = _localization.For(slot);

        controller.PrintToChat(names.Count == 0
                                   ? tr.Format(ChatTexts.SpecsNone, targetName)
                                   : tr.Format(ChatTexts.SpecsList, targetName, names.Count, ZString.Join(", ", names)));

        return ECommandAction.Handled;
    }

    // An exact name (ignoring case), else the only name containing it. Returns the text saying why there's none.
    private ChatText? FindPlayer(PlayerSlot self, string name, out PlayerSlot found)
    {
        found = default;
        var matches = 0;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.Slot == self || client.IsHltv)
            {
                continue;
            }

            if (client.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = client.Slot;

                return null;
            }

            if (client.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                found = client.Slot;
                matches++;
            }
        }

        return matches switch
        {
            1 => null,
            0 => ChatTexts.FindNone,
            _ => ChatTexts.FindMany,
        };
    }
}
