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
 
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using SurfTimer.Managers.Player;

namespace SurfTimer.Modules;

internal partial class TimerModule
{
    private void InitCommands()
    {
        _commandManager.AddClientChatCommand("r", OnCommandRestart);

        _commandManager.AddClientChatCommand("main",
                                             (player, _) =>
                                             {
                                                 Restart(player, 0);

                                                 return ECommandAction.Handled;
                                             });

        _commandManager.AddClientChatCommand("b",
                                             (player, _) =>
                                             {
                                                 Restart(player, 1);

                                                 return ECommandAction.Handled;
                                             });

        for (var i = 1; i < 24; i++)
        {
            var i1 = i;

            _commandManager.AddClientChatCommand($"b{i}",
                                                 (player, _) =>
                                                 {
                                                     Restart(player, i1);

                                                     return ECommandAction.Handled;
                                                 });
        }
    }

    private ECommandAction OnCommandRestart(IGamePlayer player, StringCommand command)
    {
        Restart(player, 0);

        return ECommandAction.Handled;
    }
}