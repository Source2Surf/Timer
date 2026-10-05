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
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Command;

internal class CommandManager : IManager, ICommandManager, IClientListener, IAdminPermissions
{
    private const string AdminManagerLibrary  = "Sharp.Modules.AdminManager";
    private const string CommandCenterLibrary = "Sharp.Modules.CommandCenter";

    private static readonly string ModuleIdentity = typeof(CommandManager).Assembly.GetName().Name!;

    private readonly Dictionary<string, AdminCommand> _adminChatCommands;
    private readonly HashSet<string>                  _registeredAdminCommands;
    private readonly HashSet<string>                  _permissions;
    private          bool                             _permissionsRegistered;
    private readonly InterfaceBridge                  _bridge;
    private readonly ISharedSystem                    _shared;

    private IModSharpModuleInterface<IAdminManager>? _adminManager;

    private readonly Dictionary<string, Func<StringCommand, ECommandAction>> _serverCommands;

    private readonly Dictionary<string, ICommandManager.ClientCommandDelegate> _clientChatCommands;
    private readonly Dictionary<string, ICommandManager.ClientCommandDelegate> _styleCommands;
    private readonly FrozenSet<char>                                           _commandTriggers;

    private readonly ILogger<CommandManager> _logger;

    public CommandManager(InterfaceBridge bridge, ISharedSystem shared, ILogger<CommandManager> logger)
    {
        _bridge        = bridge;
        _shared        = shared;
        _logger        = logger;

        // OrdinalIgnoreCase enables allocation-free ReadOnlySpan<char> alternate lookups
        // in OnClientSayCommand while keeping mixed-case chat input working.
        _clientChatCommands = new (StringComparer.OrdinalIgnoreCase);
        _styleCommands      = new (StringComparer.OrdinalIgnoreCase);
        _adminChatCommands  = new (StringComparer.OrdinalIgnoreCase);
        _serverCommands     = [];

        _registeredAdminCommands = new (StringComparer.OrdinalIgnoreCase);
        _permissions             = new (StringComparer.OrdinalIgnoreCase);

        HashSet<char> set = ['!', '/', '.', '！', '．', '／', '。'];
        _commandTriggers = set.ToFrozenSet();
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 10;

    public ECommandAction OnClientSayCommand(IGameClient client, bool teamOnly, bool isCommand, string commandName,
                                             string      message)
    {
        if (string.IsNullOrEmpty(message) || !_commandTriggers.Contains(message[0]))
        {
            return ECommandAction.Skipped;
        }

        var text = message.AsSpan(1).Trim(' ');

        var spaceIndex  = text.IndexOf(' ');
        var commandSpan = spaceIndex < 0 ? text : text[..spaceIndex];

        if (commandSpan.IsEmpty)
        {
            return ECommandAction.Skipped;
        }

        if (_styleCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out var callback)
            || _clientChatCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out callback))
        {
            return callback(client.Slot, BuildCommand(commandSpan, text, spaceIndex));
        }

        if (_adminChatCommands.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(commandSpan, out var admin))
        {
            // Registered with AdminManager: CommandCenter runs it after the permission check.
            if (_registeredAdminCommands.Contains(admin.Name))
            {
                return ECommandAction.Skipped;
            }
#if DEBUG
            _logger.LogWarning("Admin command '{cmd}' executed WITHOUT a permission check (AdminManager is not loaded, DEBUG builds only).",
                               commandSpan.ToString());

            return admin.Handler(client.Slot, BuildCommand(commandSpan, text, spaceIndex));
#else
            _logger.LogWarning("Admin command '{cmd}' ignored: ModSharp's AdminManager is not loaded.",
                               commandSpan.ToString());

            return ECommandAction.Handled;
#endif
        }

        return ECommandAction.Skipped;
    }

    private static StringCommand BuildCommand(ReadOnlySpan<char> command, ReadOnlySpan<char> text, int spaceIndex)
    {
        string? arguments = null;

        if (spaceIndex >= 0)
        {
            var argsSpan = text[(spaceIndex + 1)..].TrimStart(' ');

            if (!argsSpan.IsEmpty)
            {
                arguments = argsSpan.ToString();
            }
        }

        // Handlers switch on CommandName with lowercase literals — keep it normalized.
        return new (command.ToString().ToLowerInvariant(), true, arguments);
    }

    public void AddClientChatCommand(string command, ICommandManager.ClientCommandDelegate handler)
    {
        if (_clientChatCommands.TryAdd(command, handler))
        {
            return;
        }

        _logger.LogWarning("{cmd} is already added in _clientChatCommands.", command);
    }

    /// <remarks>
    /// Checked by ModSharp's AdminManager: a player needs any one of <paramref name="permissions"/>.
    /// Without AdminManager, admin commands only run in DEBUG builds, unchecked.
    /// </remarks>
    public void AddAdminChatCommand(string command, ImmutableArray<string> permissions, ICommandManager.ClientCommandDelegate handler)
    {
        if (_adminChatCommands.TryAdd(command, new (command, permissions, handler)))
        {
            ConnectAdminManager();

            return;
        }

        _logger.LogWarning("{cmd} is already added in _adminChatCommands.", command);
    }

    /// <returns>Whether every admin command is registered with AdminManager.</returns>
    public bool ConnectAdminManager()
    {
        if (_adminManager?.Instance is null)
        {
            _adminManager = _shared.GetSharpModuleManager()
                                   .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        }

        if (_registeredAdminCommands.Count == _adminChatCommands.Count && _permissionsRegistered)
        {
            return true;
        }

        if (_adminManager?.Instance is not { } admins)
        {
            return false;
        }

        try
        {
            var registry = admins.GetCommandRegistry(ModuleIdentity);
            registry.RegisterPermissions([.. _adminChatCommands.Values.SelectMany(x => x.Permissions).Concat(_permissions).Distinct()]);
            _permissionsRegistered = true;

            foreach (var (name, permissions, handler) in _adminChatCommands.Values)
            {
                if (!_registeredAdminCommands.Add(name))
                {
                    continue;
                }

                registry.RegisterAdminCommand(name,
                                              (client, command) =>
                                              {
                                                  if (client is not null)
                                                  {
                                                      handler(client.Slot, command);
                                                  }
                                              },
                                              permissions);
            }
        }
        catch (InvalidOperationException)
        {
            // CommandCenter isn't up yet; retried when it connects.
        }

        return _registeredAdminCommands.Count == _adminChatCommands.Count && _permissionsRegistered;
    }

    public void RegisterPermission(string permission)
    {
        if (_permissions.Add(permission))
        {
            _permissionsRegistered = false;
            ConnectAdminManager();
        }
    }

    public bool HasPermission(SteamID steamId, string permission)
        => _adminManager?.Instance?.GetAdmin(steamId)?.HasPermission(permission) is true;

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals(AdminManagerLibrary, StringComparison.OrdinalIgnoreCase)
            || name.Equals(CommandCenterLibrary, StringComparison.OrdinalIgnoreCase))
        {
            _registeredAdminCommands.Clear();
            _permissionsRegistered = false;
        }
    }

    public void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler)
    {
        if (_serverCommands.TryAdd(command, handler))
        {
            _bridge.ConVarManager.CreateServerCommand(command, handler);

            return;
        }

        _logger.LogWarning("{cmd} is already added in _serverCommands.", command);
    }

    public void AddStyleCommand(string command, ICommandManager.ClientCommandDelegate handler)
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
        ConnectAdminManager();

        return true;
    }

    public void Shutdown()
    {
        // The engine command itself goes once its last callback is released.
        foreach (var (command, handler) in _serverCommands)
        {
            _bridge.ConVarManager.ReleaseServerCommandCallback(command, handler);
        }

        _bridge.ClientManager.RemoveClientListener(this);
    }

    private readonly record struct AdminCommand(string                                Name,
                                                ImmutableArray<string>                Permissions,
                                                ICommandManager.ClientCommandDelegate Handler);
}
