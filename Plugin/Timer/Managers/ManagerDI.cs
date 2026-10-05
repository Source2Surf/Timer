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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.MapChooser;
using Source2Surf.Timer.Managers.Movement;
using Source2Surf.Timer.Managers.Patch;
using Source2Surf.Timer.Managers.Permission;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers;

internal static class ManagerDi
{
    public static void AddManagerService(this IServiceCollection services)
    {
        services.TryAddSingleton(serviceProvider => BackendOptions.FromConfiguration(
                                     serviceProvider.GetRequiredService<IConfiguration>()));
        services.AddSingleton<BackendChannel>();
        services.AddSingleton<BackendRequestManager>();
        services.AddSingleton<IRequestManager>(serviceProvider => serviceProvider.GetRequiredService<BackendRequestManager>());
        services.AddSingleton<IReplayCatalog>(serviceProvider => serviceProvider.GetRequiredService<BackendRequestManager>());
        services.AddSingleton<IRecordAdministration>(serviceProvider => serviceProvider.GetRequiredService<BackendRequestManager>());

        // The spool remains owned by the sender, rather than separately registered as IManager.
        services.AddSingleton<RunSubmissionSpool>();
        services.AddSingleton<IRunSubmissionTransportFactory, MagicOnionRunSubmissionTransportFactory>();
        services.AddSingleton<RunSubmissionSender>();
        services.AddSingleton<IManager>(serviceProvider => serviceProvider.GetRequiredService<RunSubmissionSender>());

        services.ImplSingleton<IInlineHookManager, IManager, InlineHookManager>();
        services.ImplSingleton<IPatchManager, IManager, PatchManager>();
        services.ImplSingleton<IEventHookManager, IManager, EventHookManager>();

        // After the sender: managers shut down in reverse, so login profile RPCs end before the sender
        // drains scores and closes the transport they share.
        services.ImplSingleton<IPlayerManager, IManager, PlayerManager>();

        services.AddSingleton<CommandManager>();
        services.AddSingleton<IAdminPermissions>(serviceProvider => serviceProvider.GetRequiredService<CommandManager>());
        services.ImplSingleton<ICommandManager, IManager, CommandManagerProxy>();
        services.ImplSingleton<IPermissionProvider, IManager, PermissionProviderProxy>();
        services.ImplSingleton<ILocalizationProvider, IManager, LocalizationProviderProxy>();
        services.ImplSingleton<IMapChooser, IManager, MapChooserProxy>();
        services.ImplSingleton<IMovementExtension, IManager, MovementExtensionProxy>();

        services.AddSingleton<ReplayProviderProxy>();
    }
}
