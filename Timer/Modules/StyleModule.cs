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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using SurfTimer.Managers;
using SurfTimer.Managers.Player;
using SurfTimer.Modules.Style;
using SurfTimer.Modules.Timer;
using SurfTimer.Modules.Zone;

namespace SurfTimer.Modules;

internal interface IStyleModule
{
    delegate void OnStyleLoadedDelegate(IReadOnlyList<StyleSetting> styles);

    delegate void OnClientStyleChangedDelegate(IGamePlayer player, int oldStyle, int newStyle);

    event OnStyleLoadedDelegate        OnStyleConfigLoaded;
    event OnClientStyleChangedDelegate OnClientStyleChanged;

    StyleSetting GetStyleSetting(int style);

    int GetStyleCount();
}

internal class StyleModule : IModule, IStyleModule, IGameListener
{
    private readonly InterfaceBridge _bridge;

    private readonly string _styleConfigPath;

    private readonly ICommandManager _commandManager;

    private readonly IZoneModule          _zoneModule;
    private readonly ITimerModule         _timerModule;
    private readonly IMapInfoModule       _mapInfoModule;
    private readonly ILogger<StyleModule> _logger;

    private List<StyleSetting> _styles = [];

    // ReSharper disable InconsistentNaming

    private readonly IConVar sv_autobunnyhopping;
    private readonly IConVar sv_accelerate;
    private readonly IConVar sv_friction;
    private readonly IConVar sv_enablebunnyhopping;
    private readonly IConVar sv_air_max_wishspeed;
    private readonly IConVar sv_airaccelerate;

    // ReSharper restore InconsistentNaming

    public StyleModule(InterfaceBridge      bridge,
                       ICommandManager      commandManager,
                       IZoneModule          zoneModule,
                       ITimerModule         timerModule,
                       IMapInfoModule       mapInfoModule,
                       ILogger<StyleModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _zoneModule     = zoneModule;
        _timerModule    = timerModule;
        _mapInfoModule  = mapInfoModule;
        _logger         = logger;

        _styleConfigPath = Path.Combine(bridge.TimerDataPath, "styles.jsonc");

        sv_autobunnyhopping   = InitializeConVar("sv_autobunnyhopping");
        sv_accelerate         = InitializeConVar("sv_accelerate");
        sv_friction           = InitializeConVar("sv_friction");
        sv_enablebunnyhopping = InitializeConVar("sv_enablebunnyhopping");
        sv_air_max_wishspeed  = InitializeConVar("sv_air_max_wishspeed");
        sv_airaccelerate      = InitializeConVar("sv_airaccelerate");
    }

    public bool Init()
    {
        if (!File.Exists(_styleConfigPath))
        {
            throw new FileNotFoundException($"File {_styleConfigPath} doesn't exist");
        }

        _bridge.ModSharp.InstallGameListener(this);

        _commandManager.AddServerCommand("reload_styles", OnCommandReloadStyles);

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovementPre);
        _bridge.HookManager.PlayerGetMaxSpeed.InstallHookPre(OnPlayerGetMaxSpeed);
        _bridge.HookManager.PlayerWalkMove.InstallForward(OnPlayerWalkMove);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawn);

        _timerModule.OnPlayerTimerStart += OnPlayerTimerStart;

        return true;
    }

    public void Shutdown()
    {
        sv_autobunnyhopping.Flags   |= ConVarFlags.Replicated;
        sv_accelerate.Flags         |= ConVarFlags.Replicated;
        sv_friction.Flags           |= ConVarFlags.Replicated;
        sv_enablebunnyhopping.Flags |= ConVarFlags.Replicated;
        sv_air_max_wishspeed.Flags  |= ConVarFlags.Replicated;
        sv_airaccelerate.Flags      |= ConVarFlags.Replicated;

        _bridge.ModSharp.RemoveGameListener(this);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnProcessMovementPre);
        _bridge.HookManager.PlayerGetMaxSpeed.RemoveHookPre(OnPlayerGetMaxSpeed);
        _bridge.HookManager.PlayerWalkMove.RemoveForward(OnPlayerWalkMove);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawn);
    }

    public void OnPostInit(ServiceProvider provider)
    {
        LoadStyleConfig();
    }

    public void OnServerActivate()
    {
    }

    private ECommandAction OnCommandReloadStyles(StringCommand arg)
    {
        LoadStyleConfig();

        return ECommandAction.Handled;
    }

    private HookReturnValue<float> OnPlayerGetMaxSpeed(IPlayerGetMaxSpeedHookParams @params, HookReturnValue<float> ret)
    {
        var client = @params.Client;

        if (client.IsFakeClient || _timerModule.GetTimerInfo(client.Slot) is not { } timer)
        {
            return new ();
        }

        var styleSetting = _styles[timer.Style];

        return new (EHookAction.SkipCallReturnOverride, styleSetting.RunSpeed);
    }

    private static void OnPlayerWalkMove(IPlayerWalkMoveForwardParams @params)
    {
        @params.SetSpeed(291);
    }

    private unsafe void OnProcessMovementPre(IPlayerProcessMoveForwardParams @params)
    {
        var pawn = @params.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var client = @params.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        if (_timerModule.GetTimerInfo(client.Slot) is not { } mainTimer
            || _timerModule.GetStageTimerInfo(client.Slot) is not { } stageTimer)
        {
            return;
        }

        var service = @params.Service;
        var style   = _styles[mainTimer.Style];

        sv_airaccelerate.Set(style.CustomAirAccelerate ? style.AirAccelerate : _mapInfoModule.GetDefaultAirAccelerate());

        sv_autobunnyhopping.Set(mainTimer.InZone != EZoneType.Start && style.AutoBhop);

        sv_accelerate.Set(style.Accelerate);
        sv_friction.Set(style.Friction);
        sv_air_max_wishspeed.Set(style.WishSpeed);
        sv_enablebunnyhopping.Set(style.AllowBunnyhopping);

        var mv = @params.Info;

        if (pawn.ActualMoveType == MoveType.Walk)
        {
            if (style.BlockW && (mv->ForwardMove > 0 || (service.KeyButtons & UserCommandButtons.Forward) != 0))
            {
                mv->ForwardMove    =  0;
                service.KeyButtons &= ~UserCommandButtons.Forward;
            }

            if (style.BlockS && (mv->ForwardMove < 0 || (service.KeyButtons & UserCommandButtons.Back) != 0))
            {
                mv->ForwardMove    =  0;
                service.KeyButtons &= ~UserCommandButtons.Back;
            }

            if (style.BlockA && (mv->SideMove > 0 || (service.KeyButtons & UserCommandButtons.MoveLeft) != 0))
            {
                mv->SideMove       =  0;
                service.KeyButtons &= ~UserCommandButtons.MoveLeft;
            }

            if (style.BlockD && (mv->SideMove < 0 || (service.KeyButtons & UserCommandButtons.MoveRight) != 0))
            {
                mv->SideMove       =  0;
                service.KeyButtons &= ~UserCommandButtons.MoveRight;
            }
        }
    }

    private void OnPlayerSpawn(IPlayerSpawnForwardParams param)
    {
        var client = param.Client;

        if (client.IsFakeClient || _timerModule.GetTimerInfo(client.Slot) is not { } info)
        {
            return;
        }

        ReplicateClientCvars(client, info.Style);
    }

    private void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo info)
    {
        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client)
        {
            return;
        }

        var style = _styles[info.Style];

        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString());
    }

    private IConVar InitializeConVar(string name)
    {
        var conVar = _bridge.ConVarManager.FindConVar(name)
                     ?? throw new NullReferenceException($"Failed to find {name}");

        conVar.Flags &= ~ConVarFlags.Replicated;

        return conVar;
    }

    private void LoadStyleConfig()
    {
        _styles = [new ()];

        if (!File.Exists(_styleConfigPath))
        {
            _logger.LogWarning("Style config file not found at {path}. Creating a new list with a default style.",
                               _styleConfigPath);

            File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(_styles, Utils.SerializerOptions));

            goto end;
        }

        try
        {
            var json = File.ReadAllText(_styleConfigPath);

            _styles = JsonSerializer.Deserialize<List<StyleSetting>>(json, Utils.DeserializerOptions) ?? [];

            if (_styles.Count == 0)
            {
                _logger.LogWarning("Style config is missing or empty, adding default style.");

                File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(_styles, Utils.SerializerOptions));
            }
            else if (_styles.Count > Utils.MAX_STYLE)
            {
                var count = _styles.Count;

                _logger.LogWarning("Current style count {current} exceeds allowed count {max}, removing excess styles.",
                                   count,
                                   Utils.MAX_STYLE);

                var numToRemove = count - Utils.MAX_STYLE;
                _styles.RemoveRange(Utils.MAX_STYLE, numToRemove);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize style config, using the default style setting");
        }

    end:
        AddStyleCommands();

        OnStyleConfigLoaded?.Invoke(_styles);
    }

    private void AddStyleCommands()
    {
        for (var i = 0; i < _styles.Count; i++)
        {
            var split = _styles[i].Command
                                  .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var styleIndex = i;

            foreach (var command in split)
            {
                _commandManager.AddStyleCommand(command, OnStyleCommand);
            }

            continue;

            ECommandAction OnStyleCommand(IGamePlayer player, StringCommand _)
            {
                var client = player.Client;

                if (_timerModule.GetTimerInfo(player.Slot) is not { } timerInfo
                    || _timerModule.GetStageTimerInfo(player.Slot) is not { } stageTimer
                    || player.Controller is not { } controller)
                {
                    return ECommandAction.Handled;
                }

                var oldStyle = timerInfo.Style;
                OnClientStyleChanged?.Invoke(player, oldStyle, styleIndex);
                timerInfo.ChangeStyle(styleIndex);
                stageTimer.ChangeStyle(styleIndex);
                controller.Respawn();

                ReplicateClientCvars(client, styleIndex);

                return ECommandAction.Handled;
            }
        }
    }

    private void ReplicateClientCvars(IGameClient client, int styleIndex)
    {
        var style = _styles[styleIndex];

        sv_accelerate.ReplicateToClient(client, style.Accelerate.ToString(CultureInfo.InvariantCulture));
        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString(CultureInfo.InvariantCulture));
        sv_friction.ReplicateToClient(client, style.Friction.ToString(CultureInfo.InvariantCulture));

        sv_enablebunnyhopping.ReplicateToClient(client,
                                                style.AllowBunnyhopping.ToString(CultureInfo.InvariantCulture));

        sv_air_max_wishspeed.ReplicateToClient(client, style.WishSpeed.ToString(CultureInfo.InvariantCulture));

        sv_airaccelerate.ReplicateToClient(client,
                                           (style.CustomAirAccelerate
                                               ? style.AirAccelerate
                                               : _mapInfoModule.GetDefaultAirAccelerate())
                                           .ToString(CultureInfo.InvariantCulture));
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public event IStyleModule.OnStyleLoadedDelegate? OnStyleConfigLoaded;
    public event IStyleModule.OnClientStyleChangedDelegate? OnClientStyleChanged;

    public StyleSetting GetStyleSetting(int style)
    {
        if (style < 0 || style >= _styles.Count)
        {
            throw new IndexOutOfRangeException("Style index is out of range");
        }

        return _styles[style];
    }

    public int GetStyleCount()
        => _styles.Count;
}
