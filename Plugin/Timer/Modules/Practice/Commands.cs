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
        _commandManager.AddClientChatCommand("clearloc", OnCommandClearLocs);

        // Console commands too, so a key can be bound to them (bind mouse4 saveloc); the HUD's locations panel shows
        // the key bound to each.
        AddConsoleCommand("saveloc", "Save your current location", c => SaveLoc(c));
        AddConsoleCommand("loc",     "Teleport to your current saved location", c => TeleportToLoc(c));
        AddConsoleCommand("prevloc", "Teleport to your previous saved location", c => TeleportPrev(c));
        AddConsoleCommand("nextloc", "Teleport to your next saved location", c => TeleportNext(c));
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
            controller.PrintToChat("Usage: !tele <n>  (1-based loc index)");

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

        var locs = _locs[slot];

        if (locs is null || locs.Count == 0)
        {
            controller.PrintToChat("No saved locations.");
            return ECommandAction.Handled;
        }

        var cursor = _cursor[slot];

        var sb = ZString.CreateStringBuilder(true);

        try
        {
            sb.Append("Saved locations: ");
            sb.Append(locs.Count);
            sb.Append(" (current #");
            sb.Append(cursor + 1);
            sb.Append(')');

            controller.PrintToChat(sb.ToString());
        }
        finally
        {
            sb.Dispose();
        }

        // Show the most recent few entries so chat doesn't get spammed.
        var start = locs.Count > 5 ? locs.Count - 5 : 0;

        for (var i = start; i < locs.Count; i++)
        {
            var loc = locs[i];

            var line = ZString.CreateStringBuilder(true);

            try
            {
                line.Append('#');
                line.Append(i + 1);
                line.Append(": track ");
                line.Append(loc.Track);

                if (loc.Segmented)
                {
                    line.Append(ChatColor.Grey);
                    line.Append(" (segmented)");
                    line.Append(ChatColor.White);
                }

                if (i == cursor)
                {
                    line.Append(" <- current");
                }

                controller.PrintToChat(line.ToString());
            }
            finally
            {
                line.Dispose();
            }
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandClearLocs(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { } client)
        {
            ClearLocs(client);
        }

        return ECommandAction.Handled;
    }
}
