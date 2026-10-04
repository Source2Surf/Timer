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
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;

namespace Source2Surf.Timer.Modules.Practice;

internal sealed partial class PracticeManager
{
    private void InitCommands()
    {
        _commandManager.AddClientChatCommand("saveloc",  OnCommandSaveLoc);
        _commandManager.AddClientChatCommand("save",     OnCommandSaveLoc);
        _commandManager.AddClientChatCommand("sl",       OnCommandSaveLoc);

        _commandManager.AddClientChatCommand("loc",      OnCommandLoc);
        _commandManager.AddClientChatCommand("tele",     OnCommandTele);

        _commandManager.AddClientChatCommand("nextloc",  OnCommandNextLoc);
        _commandManager.AddClientChatCommand("nl",       OnCommandNextLoc);

        _commandManager.AddClientChatCommand("prevloc",  OnCommandPrevLoc);
        _commandManager.AddClientChatCommand("pl",       OnCommandPrevLoc);

        _commandManager.AddClientChatCommand("locs",     OnCommandListLocs);
        _commandManager.AddClientChatCommand("clearloc",  OnCommandClearLocs);
        _commandManager.AddClientChatCommand("clearlocs", OnCommandClearLocs);

        // Console commands too, so a key can be bound to them (bind mouse4 saveloc); the HUD's locations panel shows
        // the key bound to each.
        AddConsoleCommand("saveloc", "Save your current location", c => SaveLoc(c));
        AddConsoleCommand("loc",     "Teleport to your current saved location", c => TeleportToLoc(c));
        AddConsoleCommand("prevloc", "Teleport to your previous saved location", c => TeleportPrev(c));
        AddConsoleCommand("nextloc", "Teleport to your next saved location", c => TeleportNext(c));
        AddConsoleCommand("clearloc", "Clear all your saved locations (run twice to confirm)", c => RequestClearLocs(c));
    }

    private readonly List<(string Name, Func<IGameClient?, StringCommand, ECommandAction> Callback)> _consoleCommands = [];

    private void AddConsoleCommand(string name, string description, Action<IGameClient> action)
    {
        ECommandAction Callback(IGameClient? client, StringCommand command)
        {
            if (client is not null)
            {
                action(client);
            }

            return ECommandAction.Handled;
        }

        _bridge.ConVarManager.CreateConsoleCommand(name, Callback, description);
        _consoleCommands.Add((name, Callback));
    }

    // The plugin reloads on every map change, so its command callbacks are handed back.
    private void ReleaseConsoleCommands()
    {
        foreach (var (name, callback) in _consoleCommands)
        {
            _bridge.ConVarManager.ReleaseConsoleCommandCallback(name, callback);
        }

        _consoleCommands.Clear();
    }

    private ECommandAction OnCommandSaveLoc(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            SaveLoc(client);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandLoc(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            TeleportToLoc(client);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTele(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        if (command.ArgCount < 1)
        {
            TeleportToLoc(client);

            return ECommandAction.Handled;
        }

        if (!command.TryGetArg<int>(1, out var n) || n < 1)
        {
            controller.PrintToChat(_localization.For(slot)[ChatTexts.UsageTele]);

            return ECommandAction.Handled;
        }

        TeleportToLoc(client, n - 1);

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandNextLoc(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            TeleportNext(client);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPrevLoc(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            TeleportPrev(client);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandListLocs(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var tr   = _localization.For(slot);
        var locs = _locs[slot];

        if (locs is null || locs.Count == 0)
        {
            controller.PrintToChat(tr[ChatTexts.LocNone]);
            return ECommandAction.Handled;
        }

        var cursor = _cursor[slot];

        controller.PrintToChat(tr.Format(ChatTexts.LocList, locs.Count, cursor + 1));

        // Show the most recent few entries so chat doesn't get spammed.
        var start = locs.Count > 5 ? locs.Count - 5 : 0;

        for (var i = start; i < locs.Count; i++)
        {
            var loc = locs[i];

            controller.PrintToChat(ZString.Concat(tr.Format(ChatTexts.LocListRow, i + 1, tr.Track(loc.Track)),
                                                  loc.Segmented ? ZString.Concat(ChatColor.Grey, tr[ChatTexts.LocListSegmented], ChatColor.White) : "",
                                                  i == cursor ? tr[ChatTexts.LocListCurrent] : ""));
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearLocs(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            RequestClearLocs(client);
        }

        return ECommandAction.Handled;
    }
}
