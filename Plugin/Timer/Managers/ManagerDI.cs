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
using Microsoft.Extensions.DependencyInjection;
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
        services.ImplSingleton<IRequestManager, IManager, RequestManagerProxy>();

        // The spool remains owned by the sender, rather than separately registered as IManager.
        // Thus the disabled/default sender performs no file I/O during Timer.Init.
        services.AddSingleton<RunSubmissionSpool>();
        services.AddSingleton(serviceProvider =>
        {
            var mode = serviceProvider.GetRequiredService<ScoreWriteModeOptions>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var options = RunSubmissionSenderOptions.FromConfiguration(configuration);

            if (mode.Mode == ScoreWriteMode.LocalSql)
            {
                // A leftover endpoint is ignored; only an explicit enabled contradicts the mode.
                if (bool.TryParse(configuration[$"{RunSubmissionSenderOptions.SectionName}:enabled"], out var enabled) && enabled)
                {
                    throw new InvalidOperationException("score_write:enabled requires score_write:mode=remote-write.");
                }

                return RunSubmissionSenderOptions.Disabled;
            }

            if (!options.Enabled)
            {
                throw new InvalidOperationException("score_write:mode=remote-write requires score_write:endpoint.");
            }

            return options;
        });
        services.AddSingleton<IRunSubmissionTransportFactory, MagicOnionRunSubmissionTransportFactory>();
        services.AddSingleton<RunSubmissionSender>();
        services.AddSingleton<IManager>(serviceProvider => serviceProvider.GetRequiredService<RunSubmissionSender>());

        services.ImplSingleton<IInlineHookManager, IManager, InlineHookManager>();
        services.ImplSingleton<IPatchManager, IManager, PatchManager>();
        services.ImplSingleton<IEventHookManager, IManager, EventHookManager>();

        // After the sender: managers shut down in reverse, so login profile RPCs end before the sender
        // drains scores and disposes the channel they share.
        services.ImplSingleton<IPlayerManager, IManager, PlayerManager>();

        services.AddSingleton<CommandManager>();
        services.ImplSingleton<ICommandManager, IManager, CommandManagerProxy>();
        services.ImplSingleton<IPermissionProvider, IManager, PermissionProviderProxy>();
        services.ImplSingleton<ILocalizationProvider, IManager, LocalizationProviderProxy>();
        services.ImplSingleton<IMapChooser, IManager, MapChooserProxy>();
        services.ImplSingleton<IMovementExtension, IManager, MovementExtensionProxy>();

        services.AddSingleton<ReplayProviderProxy>();
    }
}
