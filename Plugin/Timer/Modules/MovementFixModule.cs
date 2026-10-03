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

using Microsoft.Extensions.Logging;
using Sharp.Shared.Objects;
using Source2Surf.Timer.Managers;

namespace Source2Surf.Timer.Modules;

internal interface IMovementFixModule
{
}

// Fixes for stock CS2 movement bugs, one file per fix under MovementFix/.
internal unsafe partial class MovementFixModule : IModule, IMovementFixModule
{
    private readonly InterfaceBridge            _bridge;
    private readonly IInlineHookManager         _inlineHookManager;
    private readonly ILogger<MovementFixModule> _logger;

    private static MovementFixModule? _instance;

    // cvars
    // ReSharper disable InconsistentNaming

    private readonly IConVar timer_slopefix;

    private readonly IConVar sv_standable_normal;

    // ReSharper restore InconsistentNaming

    public MovementFixModule(InterfaceBridge            bridge,
                             IInlineHookManager         inlineHookManager,
                             ILogger<MovementFixModule> logger)
    {
        _bridge            = bridge;
        _inlineHookManager = inlineHookManager;
        _logger            = logger;

        timer_slopefix = bridge.ConVarManager.CreateConVar("timer_slopefix",
                                                           true,
                                                           "Keep the speed a downhill slope gives when landing on it without colliding with it first")
            !;

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;
    }

    public bool Init()
    {
        _instance = this;

        InstallSlopeFix();

        return true;
    }

    public void Shutdown()
    {
        // InlineHookManager shuts down first and removes the detours.
        _instance = null;
    }
}
