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
using Microsoft.Extensions.Logging;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers;

namespace Source2Surf.Timer.Modules;

internal interface IMovementFixModule
{
}

// Fixes for stock CS2 movement bugs, one file per fix under MovementFix/.
internal unsafe partial class MovementFixModule : IModule, IMovementFixModule, IGameListener
{
    private readonly InterfaceBridge            _bridge;
    private readonly IInlineHookManager         _inlineHookManager;
    private readonly ILogger<MovementFixModule> _logger;

    // cvars
    // ReSharper disable InconsistentNaming

    private readonly IConVar timer_slopefix;
    private readonly IConVar timer_telehop;
    private readonly IConVar timer_edgebug;

    private readonly IConVar sv_standable_normal;

    // ReSharper restore InconsistentNaming

    private static bool  _slopefixEnabled;
    private static bool  _telehopEnabled;
    private static bool  _edgebugEnabled;
    private static float _standableNormal;

    // CGlobalVars*, set once a map is loaded.
    private static nint _globals;

    // Counts user commands; ProcessMove runs once per sub-tick step.
    private static readonly int[]  _moveTick     = new int[PlayerSlot.MaxPlayerCount];
    private static readonly bool[] _isFakeClient = new bool[PlayerSlot.MaxPlayerCount];

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

        timer_telehop = bridge.ConVarManager.CreateConVar("timer_telehop",
                                                          true,
                                                          "Give back the speed a collision or landing took in the same tick a trigger_teleport fires")
            !;

        timer_edgebug = bridge.ConVarManager.CreateConVar("timer_edgebug",
                                                          true,
                                                          "Land players on the edge of a block instead of letting them slide off it depending on where in the tick they hit it")
            !;

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;
    }

    public bool Init()
    {
        ResetPlayerState();
        RefreshConVars();

        _bridge.ConVarManager.InstallChangeHook(timer_slopefix, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_telehop, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(timer_edgebug, OnConVarChanged);
        _bridge.ConVarManager.InstallChangeHook(sv_standable_normal, OnConVarChanged);

        InstallHooks();

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);
        _bridge.ModSharp.InstallGameListener(this);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);

        _bridge.ConVarManager.RemoveChangeHook(timer_slopefix, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_telehop, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(timer_edgebug, OnConVarChanged);
        _bridge.ConVarManager.RemoveChangeHook(sv_standable_normal, OnConVarChanged);

        // InlineHookManager shuts down first and removes the detours.
        _globals = nint.Zero;
    }

    public void OnGameInit()
        => _globals = _bridge.ModSharp.GetGlobals().GetAbsPtr();

    public void OnGameShutdown()
        => _globals = nint.Zero;

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private void OnConVarChanged(IConVar conVar)
        => RefreshConVars();

    private void RefreshConVars()
    {
        _slopefixEnabled = timer_slopefix.GetBool();
        _telehopEnabled  = timer_telehop.GetBool();
        _edgebugEnabled  = timer_edgebug.GetBool();
        _standableNormal = sv_standable_normal.GetFloat();
    }

    private static void ResetPlayerState()
    {
        Array.Fill(_moveTick, 0);
        Array.Fill(_isFakeClient, false);
        Array.Fill(_collided, false);
        Array.Fill(_speedLossTick, int.MinValue);
        Array.Fill(_teleportTick, int.MinValue);
    }

    private static HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      @params,
                                                                          HookReturnValue<EmptyHookReturn> ret)
    {
        var client = @params.Client;
        int slot   = client.Slot;

        _moveTick[slot]++;
        _isFakeClient[slot] = client.IsFakeClient;
        _collided[slot]     = false;

        return new ();
    }
}
