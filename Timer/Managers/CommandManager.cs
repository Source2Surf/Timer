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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using SurfTimer.Managers.Player;
using static SurfTimer.Managers.ICommandManager;

namespace SurfTimer.Managers;

internal interface ICommandManager
{
    delegate ECommandAction ClientCommandDelegate(IGamePlayer player, StringCommand command);

    void AddClientChatCommand(string command, ClientCommandDelegate handler);

    void AddAdminChatCommand(string command, ClientCommandDelegate handler);

    void AddClientCommandListener(string command, IClientManager.DelegateClientCommand handler);

    void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler);

    void AddStyleCommand(string command, ClientCommandDelegate handler);

    void ClearStyleCommands();
}

internal class CommandManager : IManager, ICommandManager, IClientListener
{
    private readonly Dictionary<string, ClientCommandDelegate>               _adminChatCommands;
    private readonly InterfaceBridge                                         _bridge;

    private readonly Dictionary<string, Func<StringCommand, ECommandAction>> _serverCommands;

    private readonly Dictionary<string, ClientCommandDelegate>                _clientChatCommands;
    private readonly Dictionary<string, ClientCommandDelegate>                _styleCommands;
    private readonly Dictionary<string, IClientManager.DelegateClientCommand> _clientCommandListeners;
    private readonly FrozenSet<char>                                          _commandTriggers;

    private readonly ILogger<CommandManager> _logger;

    private readonly IPlayerManager _playerManager;

    public CommandManager(InterfaceBridge bridge, ILogger<CommandManager> logger, IPlayerManager playerManager)
    {
        _bridge        = bridge;
        _logger        = logger;
        _playerManager = playerManager;

        _clientChatCommands     = [];
        _styleCommands          = [];
        _clientCommandListeners = [];
        _adminChatCommands      = [];
        _serverCommands         = [];

        HashSet<char> set = ['!', '/', '.', '！', '．', '／', '。'];
        _commandTriggers = set.ToFrozenSet();
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    public ECommandAction OnClientSayCommand(IGameClient client, bool teamOnly, bool isCommand, string commandName,
                                             string      message)
    {
        if (message.Distinct().Count() == 1 || !_commandTriggers.Contains(message[0]))
        {
            return ECommandAction.Skipped;
        }

        if (_playerManager.GetPlayer(client) is not { } player)
        {
            return ECommandAction.Stopped;
        }

        var split = message.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rawCommand = split[0][1..].ToLowerInvariant();

        var startIndex = rawCommand.Length + 1;
        var arguments  = message.Length > startIndex ? message[startIndex..] : null;

        if (_styleCommands.TryGetValue(rawCommand, out var callback)
            || _clientChatCommands.TryGetValue(rawCommand, out callback))
        {
            return callback(player, new (rawCommand, true, arguments));
        }

        if (_adminChatCommands.TryGetValue(rawCommand, out callback))
        {
            if (!player.IsAdmin)
            {
                return ECommandAction.Handled;
            }

            return callback(player, new (rawCommand, true, arguments));
        }

        return ECommandAction.Skipped;
    }

    public void AddClientChatCommand(string command, ClientCommandDelegate handler)
    {
        if (_clientChatCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("{cmd} is already added in _clientChatCommands.", command);
    }

    public void AddAdminChatCommand(string command, ClientCommandDelegate handler)
    {
        if (_adminChatCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("{cmd} is already added in _adminChatCommands.", command);
    }

    public void AddClientCommandListener(string command, IClientManager.DelegateClientCommand handler)
    {
        if (_clientCommandListeners.TryAdd(command, handler))
        {
            _bridge.ClientManager.InstallCommandListener(command, handler);

            return;
        }

        _logger.LogWarning("{cmd} is already added in _clientCommandListeners.", command);
    }

    public void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler)
    {
        if (_serverCommands.TryAdd(command, handler))
        {
            _bridge.ConVarManager.CreateServerCommand(command, handler);

            return;
        }
    }

    public void AddStyleCommand(string command, ClientCommandDelegate handler)
    {
        if (_styleCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("Style command {cmd} is already added", command);
    }

    public void ClearStyleCommands()
    {
        _styleCommands.Clear();
    }

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);

        return true;
    }

    public void Shutdown()
    {
        foreach (var (command, handler) in _clientCommandListeners)
        {
            _bridge.ClientManager.RemoveCommandListener(command, handler);
        }

        foreach (var (command, _) in _serverCommands)
        {
            _bridge.ConVarManager.ReleaseCommand(command);
        }

        _bridge.ClientManager.RemoveClientListener(this);
    }
}
