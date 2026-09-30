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
using System.Collections.Immutable;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Shared.Interfaces;

public interface ICommandManager
{
    static readonly string Identity = typeof(ICommandManager).FullName!;

    delegate ECommandAction ClientCommandDelegate(PlayerSlot slot, StringCommand command);

    void AddClientChatCommand(string command, ClientCommandDelegate handler);

    void AddAdminChatCommand(string command, ImmutableArray<string> permissions, ClientCommandDelegate handler);

    void AddServerCommand(string command, Func<StringCommand, ECommandAction> handler);

    void AddStyleCommand(string command, ClientCommandDelegate handler);

    void ClearStyleCommands();
}
