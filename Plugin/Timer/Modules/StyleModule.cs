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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models.Style;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IStyleModule
{
    void RegisterListener(IStyleModuleListener listener);

    void UnregisterListener(IStyleModuleListener listener);

    StyleSetting GetStyleSetting(int style);

    /// <summary>
    ///     The styles players can pick, in the config's order.
    /// </summary>
    IReadOnlyList<int> GetStyleIds();

    /// <summary>
    ///     The first of them, which players start on.
    /// </summary>
    int DefaultStyle { get; }

    bool IsStyleEnabled(int style);

    /// <summary>
    ///     The style if players can pick it, else the default.
    /// </summary>
    int ValidStyle(int style);

    /// <summary>
    ///     The next or previous style players can pick, stopping at the ends.
    /// </summary>
    int StepStyle(int style, int step);

    /// <summary>
    ///     Puts the player on the style, stopping their timers and respawning them, as its chat command does. False when
    ///     players can't pick it.
    /// </summary>
    bool SwitchStyle(PlayerSlot slot, int style);

    /// <summary>
    ///     Plain !style, for a style menu to open; without a handler the styles are listed in chat.
    /// </summary>
    event Action<PlayerSlot>? StyleMenuRequested;
}

internal class StyleModule : IModule, IStyleModule, ITimerStyles, IGameListener, ITimerModuleListener, IZoneModuleListener
{
    private readonly InterfaceBridge _bridge;

    private readonly string _styleConfigPath;

    private readonly ICommandManager       _commandManager;
    private readonly IEventHookManager     _eventHook;
    private readonly ILocalizationProvider _localization;

    private readonly IZoneModule          _zoneModule;
    private          ITimerModule         _timerModule = null!;
    private readonly IMapInfoModule       _mapInfoModule;
    private readonly IRequestManager      _request;
    private readonly ILogger<StyleModule> _logger;
    private readonly ListenerHub<IStyleModuleListener> _listenerHub;

    private StyleSetting?[] _byId = new StyleSetting?[TimerConstants.MAX_STYLE];
    private int[]           _ids  = [];

    // ReSharper disable InconsistentNaming

    private readonly IConVar sv_autobunnyhopping;
    private readonly IConVar sv_accelerate;
    private readonly IConVar sv_friction;
    private readonly IConVar sv_enablebunnyhopping;
    private readonly IConVar sv_air_max_wishspeed;
    private readonly IConVar sv_airaccelerate;

    // ReSharper restore InconsistentNaming

    // Cached last-applied style key to skip redundant Set() calls across players.
    // Combines style index + inStartZone flag into a single value.
    // Only when the composite key changes do we call Set() on all ConVars.
    // By slot: the keys force_hsw 2 or a_or_d_only settled on this run, -1 until then.
    private readonly int[] _keyCombo = Enumerable.Repeat(-1, PlayerSlot.MaxPlayerCount).ToArray();

    public StyleModule(InterfaceBridge       bridge,
                       ICommandManager       commandManager,
                       IZoneModule           zoneModule,
                       IMapInfoModule        mapInfoModule,
                       IRequestManager       request,
                       ILocalizationProvider localization,
                       IEventHookManager     eventHook,
                       ILogger<StyleModule>  logger)
    {
        _bridge         = bridge;
        _eventHook      = eventHook;
        _localization   = localization;
        _commandManager = commandManager;
        _zoneModule     = zoneModule;
        _mapInfoModule  = mapInfoModule;
        _request        = request;
        _logger      = logger;
        _listenerHub = new ListenerHub<IStyleModuleListener>(logger);

        var configDir = Path.Combine(bridge.SharpPath, "configs");
        Directory.CreateDirectory(configDir);
        _styleConfigPath = Path.Combine(configDir, "timer-styles.jsonc");

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
        _commandManager.AddClientChatCommand("style", OnCommandStyle);
        _eventHook.ListenEvent("player_jump", OnPlayerJump);

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnProcessMovementPre);
        _bridge.HookManager.PlayerGetMaxSpeed.InstallHookPre(OnPlayerGetMaxSpeed);
        _bridge.HookManager.PlayerWalkMove.InstallForward(OnPlayerWalkMove);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawn);

        return true;
    }

    public void Shutdown()
    {
        _commandManager.ClearStyleCommands();

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

        _timerModule.UnregisterListener(this);
        _zoneModule.UnregisterListener(this);
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _timerModule = provider.GetRequiredService<ITimerModule>();
        _timerModule.RegisterListener(this);
        _zoneModule.RegisterListener(this);
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

        var styleSetting = GetStyleOrDefault(timer.Style);

        return new (EHookAction.SkipCallReturnOverride, styleSetting.RunSpeed);
    }

    // CS2's max ground speed for the knife (250 * 1.164 speed modifier ≈ 291) — keeps
    // walk-move from being capped below what surf ramps launch players at.
    private const int WalkMoveSpeed = 291;

    private static void OnPlayerWalkMove(IPlayerWalkMoveForwardParams @params)
    {
        @params.SetSpeed(WalkMoveSpeed);
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
        var style   = GetStyleOrDefault(mainTimer.Style);

        var inStartZone = mainTimer.InZone == EZoneType.Start;

        // The movement cvars are global, set before each player's movement. Only the ones that differ from the last
        // player's are written: each write is a native call with the engine's change callbacks.
        SetIfChanged(sv_airaccelerate, style.CustomAirAccelerate ? style.AirAccelerate : _mapInfoModule.GetDefaultAirAccelerate());
        SetIfChanged(sv_autobunnyhopping, !inStartZone && style.AutoBhop);
        SetIfChanged(sv_accelerate, style.Accelerate);
        SetIfChanged(sv_friction, style.Friction);
        SetIfChanged(sv_air_max_wishspeed, style.WishSpeed ?? _mapInfoModule.GetGameModeWishSpeed());
        SetIfChanged(sv_enablebunnyhopping, style.AllowBunnyhopping);

        var mv = @params.Info;

        if (pawn.ActualMoveType == MoveType.Walk)
        {
            // As bhoptimer: a map that puts gravity back to normal gets the style's again.
            if (style.Gravity != 1f && pawn.GravityScale is 1f or 0f)
            {
                pawn.SetGravityScale(style.Gravity);
            }

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

            if ((style.ForceHsw > 0 || style.AOrDOnly || style.ForceBackwards)
                && (style.ForceGroundKeys || !pawn.GroundEntityHandle.IsValid()))
            {
                ApplyInputRules(client, style, mv, service, inStartZone);
            }
        }
    }

    private static void SetIfChanged(IConVar cvar, float value)
    {
        if (cvar.GetFloat() != value)
        {
            cvar.Set(value);
        }
    }

    private static void SetIfChanged(IConVar cvar, bool value)
    {
        if (cvar.GetBool() != value)
        {
            cvar.Set(value);
        }
    }

    // As bhoptimer's: input the style doesn't allow is dropped. Left is a positive SideMove.
    private unsafe void ApplyInputRules(IGameClient client, StyleSetting style, MoveData* mv, IPlayerMovementService service,
                                        bool inStartZone)
    {
        var keys    = service.KeyButtons;
        var forward = mv->ForwardMove > 0 && (keys & UserCommandButtons.Forward) != 0;
        var back    = mv->ForwardMove < 0 && (keys & UserCommandButtons.Back) != 0;
        var left    = mv->SideMove > 0 && (keys & UserCommandButtons.MoveLeft) != 0;
        var right   = mv->SideMove < 0 && (keys & UserCommandButtons.MoveRight) != 0;
        var slot    = client.Slot;

        var verdict = new InputVerdict(false, false, _keyCombo[slot]);

        if (style.ForceHsw > 0)
        {
            verdict = Hsw(style.ForceHsw, verdict.Combo, inStartZone, forward, back, left, right);

            // Surf HSW tells the player which pair it settled on.
            if (_keyCombo[slot] == -1 && verdict.Combo != -1 && _bridge.TryGetController(slot, out var controller))
            {
                controller.PrintToChat(_localization.For(slot)[verdict.Combo == 0 ? ChatTexts.ShswCombo0 : ChatTexts.ShswCombo1]);
            }
        }
        else if (style.AOrDOnly)
        {
            verdict = AOrD(verdict.Combo, left, right);
        }

        if (style.ForceBackwards && !Backwards(mv->ViewAngles.Y, mv->Velocity))
        {
            verdict = verdict with { DropForward = true, DropSide = true };
        }

        _keyCombo[slot] = verdict.Combo;

        if (verdict.DropForward)
        {
            mv->ForwardMove    =  0;
            service.KeyButtons &= ~(UserCommandButtons.Forward | UserCommandButtons.Back);
        }

        if (verdict.DropSide)
        {
            mv->SideMove       =  0;
            service.KeyButtons &= ~(UserCommandButtons.MoveLeft | UserCommandButtons.MoveRight);
        }
    }

    internal readonly record struct InputVerdict(bool DropForward, bool DropSide, int Combo);

    // force_hsw 1: W with A or D. Surf HSW (2) out of the start zone: W+A/S+D (0) or W+D/S+A (1), the first used.
    internal static InputVerdict Hsw(int mode, int combo, bool inStartZone, bool forward, bool back, bool left, bool right)
    {
        if (mode == 2 && !inStartZone)
        {
            var pair = (forward && left) || (back && right) ? 0
                : (forward && right) || (back && left)     ? 1
                                                             : -1;

            if (combo == -1)
            {
                combo = pair;
            }

            var drop = pair == -1 || pair != combo;

            return new (drop, drop, combo);
        }

        var side = left || right;

        return new ((back && side) || (forward && !side), side && !forward, combo);
    }

    // A or D, whichever comes first.
    internal static InputVerdict AOrD(int combo, bool left, bool right)
    {
        var pair = left ? 0 : right ? 1 : -1;

        if (combo == -1)
        {
            combo = pair;
        }

        return new (false, pair != -1 && pair != combo, combo);
    }

    // The view at most this dot from the horizontal velocity, about 143° away; standing still counts.
    private const float BackwardsMaxDot = -0.8f;

    internal static bool Backwards(float viewYaw, Vector velocity)
    {
        var speed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));

        if (speed == 0f)
        {
            return true;
        }

        var yaw = viewYaw * (MathF.PI / 180f);

        return ((MathF.Cos(yaw) * velocity.X) + (MathF.Sin(yaw) * velocity.Y)) / speed <= BackwardsMaxDot;
    }

    // Each jump's velocity changes, a frame after it as bhoptimer does, outside the movement.
    private void OnPlayerJump(IGameEvent e)
    {
        if (e.GetPlayerController("userid") is not { IsValidEntity: true } controller
            || controller.IsFakeClient
            || _timerModule.GetTimerInfo(controller.PlayerSlot) is not { } timer
            || GetStyleSetting(timer.Style) is not { ChangesJumps: true } style)
        {
            return;
        }

        var slot = controller.PlayerSlot;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            if (_bridge.TryGetController(slot, out var deferred)
                && deferred.GetPlayerPawn() is { IsAlive: true } pawn)
            {
                pawn.SetAbsVelocity(JumpVelocity(pawn.GetAbsVelocity(), style));
            }
        });
    }

    internal static Vector JumpVelocity(Vector velocity, StyleSetting style)
    {
        var speed = MathF.Sqrt((velocity.X * velocity.X) + (velocity.Y * velocity.Y));
        var scale = 1f;

        if (speed > 0f)
        {
            if (style.VelocityMultiplier != 0f)
            {
                scale *= style.VelocityMultiplier;
            }

            if (style.BonusVelocity != 0f)
            {
                scale *= (speed + style.BonusVelocity) / speed;
            }

            if (style.MinVelocity > 0f && speed < style.MinVelocity)
            {
                scale *= style.MinVelocity / speed;
            }
        }

        var z = velocity.Z;

        if (style.JumpMultiplier != 0f)
        {
            z *= style.JumpMultiplier;
        }

        z += style.JumpBonus;

        var limited = speed * scale;

        if (style.VelocityLimit > 0f && limited > style.VelocityLimit)
        {
            scale *= style.VelocityLimit / limited;
        }

        return new (velocity.X * scale, velocity.Y * scale, z);
    }

    private void OnPlayerSpawn(IPlayerSpawnForwardParams param)
    {
        var client = param.Client;

        if (client.IsFakeClient || _timerModule.GetTimerInfo(client.Slot) is not { } info)
        {
            return;
        }

        ReplicateClientCvars(client, info.Style);
        ApplyStylePhysics(param.Controller, param.Pawn, GetStyleOrDefault(info.Style));
    }

    private static void ApplyStylePhysics(IPlayerController controller, IPlayerPawn pawn, StyleSetting style)
    {
        if (pawn.GravityScale != style.Gravity)
        {
            pawn.SetGravityScale(style.Gravity);
        }

        var speed = style.Speed * style.TimerScale;

        if (controller.LaggedMovement != speed)
        {
            controller.LaggedMovement = speed;
        }
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo info)
    {
        var slot = controller.PlayerSlot;
        _keyCombo[slot] = -1;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client)
        {
            return;
        }

        var style = GetStyleOrDefault(info.Style);

        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString());

        // Normal styles leave a map's gravity and speed alone.
        if (style.Gravity != 1f || style.Speed * style.TimerScale != 1f)
        {
            ApplyStylePhysics(controller, pawn, style);
        }
    }

    public void OnZoneStartTouch(IZoneInfo zoneInfo, IPlayerController controller, IPlayerPawn pawn)
    {
        if (zoneInfo.ZoneType != EZoneType.Start)
        {
            return;
        }

        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client || client.IsFakeClient)
        {
            return;
        }

        sv_autobunnyhopping.ReplicateToClient(client, false.ToString());
    }

    public void OnZoneEndTouch(IZoneInfo zoneInfo, IPlayerController controller, IPlayerPawn pawn)
    {
        if (zoneInfo.ZoneType != EZoneType.Start)
        {
            return;
        }

        var slot = controller.PlayerSlot;

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client || client.IsFakeClient)
        {
            return;
        }

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo)
        {
            return;
        }

        var style = GetStyleOrDefault(timerInfo.Style);

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
        _commandManager.ClearStyleCommands();

        List<StyleSetting> styles = [new ()];

        if (!File.Exists(_styleConfigPath))
        {
            _logger.LogWarning("Style config file not found at {path}. Creating a new list with a default style.",
                               _styleConfigPath);

            File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(styles, Utils.SerializerOptions));
        }
        else
        {
            try
            {
                var json = File.ReadAllText(_styleConfigPath);

                styles = JsonSerializer.Deserialize<List<StyleSetting>>(json, Utils.DeserializerOptions) ?? [];

                if (styles.Count == 0)
                {
                    _logger.LogWarning("Style config is missing or empty, adding default style.");

                    styles = [new ()];
                    File.WriteAllText(_styleConfigPath, JsonSerializer.Serialize(styles, Utils.SerializerOptions));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize style config, using the default style setting");
                styles = [new ()];
            }
        }

        (_byId, _ids) = ResolveStyles(styles, _logger);

        AddStyleCommands();
        MovePlayersOffGoneStyles();

        var configured = _byId.OfType<StyleSetting>().ToList();
        NotifyStyleConfigLoaded(configured);
        _ = RegisterStyleFactorsAsync(configured);
    }

    /// <summary>
    ///     Each style under its id: the config's, or its place in the list. One with an id out of range or already
    ///     taken is left out. With none enabled, the first is picked anyway, or a default style if none is left.
    /// </summary>
    internal static (StyleSetting?[] ById, int[] Enabled) ResolveStyles(IReadOnlyList<StyleSetting> styles, ILogger logger)
    {
        var byId    = new StyleSetting?[TimerConstants.MAX_STYLE];
        var enabled = new List<int>();

        for (var i = 0; i < styles.Count; i++)
        {
            var id = styles[i].Id >= 0 ? styles[i].Id : i;

            if (id >= TimerConstants.MAX_STYLE || byId[id] is not null)
            {
                logger.LogError("Style \"{Name}\" is left out: its id {Id} is {Why}",
                                styles[i].Name,
                                id,
                                id >= TimerConstants.MAX_STYLE ? $"past {TimerConstants.MAX_STYLE - 1}" : "another style's");

                continue;
            }

            byId[id] = styles[i] with { Id = id };

            if (styles[i].Enabled)
            {
                enabled.Add(id);
            }
        }

        if (enabled.Count == 0)
        {
            var first = Array.FindIndex(byId, s => s is not null);

            if (first < 0)
            {
                first       = 0;
                byId[first] = new () { Id = first };
            }

            logger.LogWarning("No style is enabled; enabling \"{Name}\" (id {Id})", byId[first]!.Name, first);
            enabled.Add(first);
        }

        return (byId, enabled.ToArray());
    }

    // A reload can take a player's style away; they go to the default.
    private void MovePlayersOffGoneStyles()
    {
        for (var i = 0; i < PlayerSlot.MaxPlayerCount; i++)
        {
            var slot = (PlayerSlot) i;

            if (_timerModule.GetTimerInfo(slot) is { } timer && !IsStyleEnabled(timer.Style))
            {
                NotifyClientStyleChanged(slot, timer.Style, DefaultStyle);
                timer.ChangeStyle(DefaultStyle);
                _timerModule.GetStageTimerInfo(slot)?.ChangeStyle(DefaultStyle);
            }
        }
    }

    // The backend scores with these unless it configures its own StyleFactors.
    private async Task RegisterStyleFactorsAsync(IReadOnlyList<StyleSetting> styles)
    {
        var factors = new Dictionary<int, double>(styles.Count);

        foreach (var style in styles)
        {
            factors[style.Id] = style.ScoreFactor;
        }

        try
        {
            if (!await RetryHelper.RetryAsync(() => _request.RegisterStyleFactors(factors), RetryHelper.IsTransient, _logger, "RegisterStyleFactors")
                                  .ConfigureAwait(false))
            {
                _logger.LogInformation("Timer.Backend scores with its own StyleFactors, not timer-styles.jsonc's score_factor");
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to register the styles' score factors with Timer.Backend");
        }
    }

    private void AddStyleCommands()
    {
        foreach (var id in _ids)
        {
            var split = _byId[id]!.Command
                                  .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var styleIndex = id;

            foreach (var command in split)
            {
                _commandManager.AddStyleCommand(command, OnStyleCommand);
            }

            continue;

            ECommandAction OnStyleCommand(PlayerSlot slot, StringCommand _)
            {
                SwitchStyle(slot, styleIndex);

                return ECommandAction.Handled;
            }
        }
    }

    public event Action<PlayerSlot>? StyleMenuRequested;

    public bool SwitchStyle(PlayerSlot slot, int style)
    {
        if (!IsStyleEnabled(style)
            || _bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true }
            || _timerModule.GetTimerInfo(slot) is not { } timerInfo
            || _timerModule.GetStageTimerInfo(slot) is not { } stageTimer)
        {
            return false;
        }

        NotifyClientStyleChanged(slot, timerInfo.Style, style);
        timerInfo.ChangeStyle(style);
        stageTimer.ChangeStyle(style);

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            if (_bridge.ClientManager.GetGameClient(slot) is not { } deferredClient)
            {
                return;
            }

            if (deferredClient.GetPlayerController() is not { IsValidEntity: true } deferredController)
            {
                return;
            }

            if (deferredController.Team <= CStrikeTeam.Spectator)
            {
                deferredController.SwitchTeam((CStrikeTeam) Random.Shared.Next(2, 4));
            }

            deferredController.Respawn();

            ReplicateClientCvars(deferredClient, style);
        });

        return true;
    }

    // !style lists the styles players can pick; !style <name or command> switches to one.
    private ECommandAction OnCommandStyle(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var arg = command.ArgString.Trim();

        if (arg.Length > 0 && FindStyle(arg) is { } style)
        {
            SwitchStyle(slot, style);

            return ECommandAction.Handled;
        }

        if (arg.Length == 0 && StyleMenuRequested is { } openMenu)
        {
            openMenu(slot);

            return ECommandAction.Handled;
        }

        var tr      = _localization.For(slot);
        var current = _timerModule.GetTimerInfo(slot)?.Style ?? DefaultStyle;

        controller.PrintToChat(tr.Format(ChatTexts.StyleList, Utils.Highlight(GetStyleSetting(current).Name)));

        foreach (var id in _ids)
        {
            var setting = _byId[id]!;
            var aliases = string.Join(' ', setting.Command.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                         .Select(c => "!" + c));

            controller.PrintToChat(setting.Description.Length > 0
                                       ? tr.Format(ChatTexts.StyleRowDescribed, Utils.Highlight(setting.Name), aliases,
                                                   ChatColorTags.Apply(setting.Description))
                                       : tr.Format(ChatTexts.StyleRow, Utils.Highlight(setting.Name), aliases));
        }

        return ECommandAction.Handled;
    }

    // An enabled style by its name or one of its commands, in any case.
    internal int? FindStyle(string nameOrCommand)
    {
        foreach (var id in _ids)
        {
            var setting = _byId[id]!;

            if (setting.Name.Equals(nameOrCommand, StringComparison.OrdinalIgnoreCase)
                || setting.Command.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Any(c => c.Equals(nameOrCommand.TrimStart('!'), StringComparison.OrdinalIgnoreCase)))
            {
                return id;
            }
        }

        return null;
    }

    private void ReplicateClientCvars(IGameClient client, int styleIndex)
    {
        var style = GetStyleOrDefault(styleIndex);

        sv_accelerate.ReplicateToClient(client, style.Accelerate.ToString(CultureInfo.InvariantCulture));
        sv_autobunnyhopping.ReplicateToClient(client, style.AutoBhop.ToString(CultureInfo.InvariantCulture));
        sv_friction.ReplicateToClient(client, style.Friction.ToString(CultureInfo.InvariantCulture));

        sv_enablebunnyhopping.ReplicateToClient(client,
                                                style.AllowBunnyhopping.ToString(CultureInfo.InvariantCulture));

        sv_air_max_wishspeed.ReplicateToClient(client, (style.WishSpeed ?? _mapInfoModule.GetGameModeWishSpeed()).ToString(CultureInfo.InvariantCulture));

        sv_airaccelerate.ReplicateToClient(client,
                                           (style.CustomAirAccelerate
                                               ? style.AirAccelerate
                                               : _mapInfoModule.GetDefaultAirAccelerate())
                                           .ToString(CultureInfo.InvariantCulture));
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void RegisterListener(IStyleModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IStyleModuleListener listener)
        => _listenerHub.Unregister(listener);

    private void NotifyStyleConfigLoaded(IReadOnlyList<StyleSetting> styles)
        => _listenerHub.NotifyAll("OnStyleConfigLoaded",
                                  static (l, s) => l.OnStyleConfigLoaded(s),
                                  styles);

    private void NotifyClientStyleChanged(PlayerSlot slot, int oldStyle, int newStyle)
        => _listenerHub.NotifyAll("OnClientStyleChanged",
                                  static (l, s, o, n) => l.OnClientStyleChanged(s, o, n),
                                  slot, oldStyle, newStyle);

    // A disabled style's setting is still there for its runs; an unknown one falls back to the default, as callers
    // like MiscModule's per-tick hooks can hold one that a reload took away.
    public StyleSetting GetStyleSetting(int style)
        => ((uint) style < (uint) _byId.Length ? _byId[style] : null)
           ?? (_ids.Length > 0 ? _byId[_ids[0]] : null)
           ?? new StyleSetting();

    private StyleSetting GetStyleOrDefault(int style)
        => GetStyleSetting(style);

    public StyleSetting? GetPlayerStyle(PlayerSlot slot)
        => _timerModule.GetTimerInfo(slot) is { } timer ? GetStyleSetting(timer.Style) : null;

    public IReadOnlyList<int> GetStyleIds()
        => _ids;

    public int DefaultStyle
        => _ids.Length > 0 ? _ids[0] : 0;

    public bool IsStyleEnabled(int style)
        => Array.IndexOf(_ids, style) >= 0;

    public int ValidStyle(int style)
        => IsStyleEnabled(style) ? style : DefaultStyle;

    public int StepStyle(int style, int step)
    {
        var at = Array.IndexOf(_ids, style);

        return at < 0 ? DefaultStyle : _ids[Math.Clamp(at + step, 0, _ids.Length - 1)];
    }
}
