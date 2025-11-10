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

using Microsoft.Extensions.DependencyInjection;

namespace SurfTimer.Managers;

internal static class ManagerDi
{
    public static void AddManagerService(this IServiceCollection services)
    {
        services.ImplSingleton<IRequestManager, IManager, RequestManagerLiteDB>();

        services.ImplSingleton<IInlineHookManager, IManager, InlineHookManager>();
        services.ImplSingleton<IEventHookManager, IManager, EventHookManager>();

        services.ImplSingleton<IPlayerManager, IManager, PlayerManager>();
        services.ImplSingleton<ICommandManager, IManager, CommandManager>();
    }
}
