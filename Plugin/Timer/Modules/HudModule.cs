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
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal interface IHudModule
{
}

/// <summary>
///     The timer HUD: a Panorama layout (panorama/layout/custom_game/surftimer/hud.xml, which clients get from the
///     server's workshop addon) on a <c>custom_hud_layout</c> entity of each player's own, networked only to them.
///     The entity's whole state is that player's, so every value and class is set on it directly, and nothing one
///     player's HUD does costs anyone else bandwidth.
/// </summary>
internal partial class HudModule : IModule, IHudModule, IPlayerSettings, ITimerModuleListener, IZoneModuleListener, IPlayerManagerListener
{
    private const float HudUpdateInterval   = 0.10f; // seconds between a player's HUD refreshes
    private const float LayoutRetryInterval = 5f;    // after the layout entity failed to spawn
    private const float SaveDelay           = 1f;    // settings are written this long after the last change

    private const string DefaultLayout = "panorama/layout/custom_game/surftimer/hud.vxml_c"; // the compiled resource name

    private readonly InterfaceBridge    _bridge;
    private readonly IPanoramaManager   _panorama;
    private readonly ITransmitManager   _transmit;
    private readonly ITimerModule       _timerModule;
    private readonly IReplayModule      _replayModule;
    private readonly ICentralReplay     _central;
    private readonly IPersonalBestReplays _personalBests;
    private readonly ILocalizationProvider _localization;
    private readonly IMapChooser        _mapChooser;
    private readonly IRecordModule      _recordModule;
    private readonly IZoneModule        _zoneModule;
    private readonly IMapInfoModule     _mapInfo;
    private readonly IStyleModule       _styleModule;
    private readonly IPracticeModule    _practiceModule;
    private readonly IPlayerManager     _playerManager;
    private readonly ICommandManager    _commandManager;
    private readonly IRequestManager    _request;
    private readonly IEventHookManager  _eventHook;
    private readonly ILogger<HudModule> _logger;

    private readonly HudPlayer?[] _players = new HudPlayer?[PlayerSlot.MaxPlayerCount];

    // The keys panel's turn arrows, sampled every tick for every pawn (bots too, for spectated replays).
    private readonly float[] _keyYaw = new float[PlayerSlot.MaxPlayerCount];
    private readonly int[]   _turn   = new int[PlayerSlot.MaxPlayerCount]; // 1 turning left, -1 right, 0 not turning
    private readonly float[] _turnAt = new float[PlayerSlot.MaxPlayerCount];

    // ReSharper disable InconsistentNaming
    private readonly IConVar  timer_hud_layout;
    private readonly IConVar? sv_air_max_wishspeed;

    private float _airMaxWish = DefaultAirMaxWish;

    // ReSharper restore InconsistentNaming

    public HudModule(InterfaceBridge    bridge,
                     ISharedSystem      shared,
                     ITimerModule       timerModule,
                     IReplayModule      replayModule,
                     ICentralReplay     central,
                     IPersonalBestReplays personalBests,
                     ILocalizationProvider localization,
                     IMapChooser        mapChooser,
                     IRecordModule      recordModule,
                     IZoneModule        zoneModule,
                     IMapInfoModule     mapInfo,
                     IStyleModule       styleModule,
                     IPracticeModule    practiceModule,
                     IPlayerManager     playerManager,
                     ICommandManager    commandManager,
                     IRequestManager    request,
                     IEventHookManager  eventHook,
                     ILogger<HudModule> logger)
    {
        _bridge         = bridge;
        _panorama       = shared.GetPanoramaManager();
        _transmit       = shared.GetTransmitManager();
        _timerModule    = timerModule;
        _replayModule   = replayModule;
        _central        = central;
        _personalBests  = personalBests;
        _localization   = localization;
        _mapChooser     = mapChooser;
        _recordModule   = recordModule;
        _zoneModule     = zoneModule;
        _mapInfo        = mapInfo;
        _styleModule    = styleModule;
        _practiceModule = practiceModule;
        _playerManager  = playerManager;
        _commandManager = commandManager;
        _request        = request;
        _eventHook      = eventHook;
        _logger         = logger;

        timer_hud_layout = bridge.ConVarManager.CreateConVar("timer_hud_layout",
                                                             DefaultLayout,
                                                             "Panorama layout of the timer HUD. Clients must have it mounted (workshop addon).")!;

        sv_air_max_wishspeed = bridge.ConVarManager.FindConVar("sv_air_max_wishspeed");
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnSsjMovePre);
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnSsjMovePost);
        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);
        _panorama.InstallClickListener(OnHudClicked);
        _eventHook.ListenEvent("player_jump", OnSsjJump);

        if (sv_air_max_wishspeed is not null)
        {
            _airMaxWish = sv_air_max_wishspeed.GetFloat();
            _bridge.ConVarManager.InstallChangeHook(sv_air_max_wishspeed, OnAirMaxWishChanged);
        }

        _timerModule.RegisterListener(this);
        _zoneModule.RegisterListener(this);
        _zoneModule.EditorRequested += OnZoneEditorRequested;
        _recordModule.LeaderboardRequested += OnLeaderboardRequested;
        _styleModule.StyleMenuRequested    += OnStyleMenuRequested;
        _mapInfo.MapInfoRequested          += OnMapInfoRequested;
        _playerManager.RegisterListener(this);

        _commandManager.AddClientChatCommand("hud", OnCommandHud);
        _commandManager.AddClientChatCommand("showkeys", OnCommandShowKeys);
        _commandManager.AddClientChatCommand("replay", OnCommandReplay);
        _commandManager.AddClientChatCommand("profile", OnCommandProfile);
        _commandManager.AddClientChatCommand("stats", OnCommandProfile);
        _commandManager.AddClientChatCommand("ssj", OnCommandSsj);

        return true;
    }

    public void Shutdown()
    {
        _timerModule.UnregisterListener(this);
        _zoneModule.UnregisterListener(this);
        _zoneModule.EditorRequested -= OnZoneEditorRequested;
        _recordModule.LeaderboardRequested -= OnLeaderboardRequested;
        _styleModule.StyleMenuRequested    -= OnStyleMenuRequested;
        _mapInfo.MapInfoRequested          -= OnMapInfoRequested;
        _playerManager.UnregisterListener(this);

        _panorama.RemoveClickListener(OnHudClicked);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnSsjMovePre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnSsjMovePost);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);

        if (sv_air_max_wishspeed is not null)
        {
            _bridge.ConVarManager.RemoveChangeHook(sv_air_max_wishspeed, OnAirMaxWishChanged);
        }

        foreach (var p in _players)
        {
            if (p is null)
            {
                continue;
            }

            FlushSettings(p, true);
            RemoveEntities(p);
        }

        Array.Clear(_players);
    }

    // ------------------------------------------------------------------ players

    public void OnClientPutInServer(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client)
        {
            return;
        }

        // Put in again (a map change), or a previous occupant's state if its disconnect went unseen.
        if (_players[slot] is { } previous)
        {
            FlushSettings(previous);
            RemoveEntities(previous);
        }

        var p = new HudPlayer(slot, _bridge.GlobalVars.CurTime);
        p.Tr           = new HudTr(key => _localization.GetText(slot, key));
        _players[slot] = p;

        LoadSettings(p, client.SteamId);
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        if (_players[slot] is not { } p)
        {
            return;
        }

        FlushSettings(p);
        RemoveEntities(p);
        _players[slot] = null;
    }

    // The SteamID can be missing when the player is put in the server; this is the second chance.
    public void OnClientInfoLoaded(SteamID steamId)
    {
        if (_bridge.ClientManager.GetGameClient(steamId) is { } client
            && _players[client.Slot] is { SettingsLoaded: false } p)
        {
            LoadSettings(p, steamId);
        }
    }

    // Defaults show until the backend answers.
    private void LoadSettings(HudPlayer p, SteamID steamId)
    {
        var id = (ulong) steamId;

        if (id == 0)
        {
            return;
        }

        p.SteamId        = id;
        p.SettingsLoaded = true;

        Task.Run(async () =>
        {
            byte[]? data = null;

            try
            {
                data = await RetryHelper.RetryAsync(() => _request.GetPlayerSettings(steamId), RetryHelper.IsTransient, _logger, "GetPlayerSettings")
                                        .ConfigureAwait(false);
            }
            catch (RpcException e) when (e.StatusCode == StatusCode.Unimplemented)
            {
                // a backend without player settings: defaults
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to load settings for {SteamId}", id);
            }

            if (data is not { Length: > 0 })
            {
                return;
            }

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                // Settings they changed meanwhile win over the saved ones.
                if (_players[p.Slot] != p || p.SettingsChanged)
                {
                    return;
                }

                PlayerSettingsCodec.Decode(data, p);

                foreach (var target in HudTargets.All)
                {
                    ClampPosition(p, target);
                }

                p.MenuDirty = true;
                Changed?.Invoke(p.Slot);
            }).ConfigureAwait(false);
        });
    }

    private void MarkSettingsChanged(HudPlayer p)
    {
        p.SaveAt          = _bridge.GlobalVars.CurTime + SaveDelay;
        p.SettingsChanged = true;
        Changed?.Invoke(p.Slot);
    }

    // Encoded here, sent in the background. Shutting down waits a little for it, not for long.
    private void FlushSettings(HudPlayer p, bool wait = false)
    {
        if (float.IsNaN(p.SaveAt))
        {
            return;
        }

        p.SaveAt = float.NaN;

        if (p.SteamId == 0)
        {
            return;
        }

        var steamId = new SteamID(p.SteamId);
        var data    = PlayerSettingsCodec.Encode(p);

        var save = Task.Run(async () =>
        {
            try
            {
                await RetryHelper.RetryAsync(() => _request.SavePlayerSettings(steamId, data), RetryHelper.IsTransient, _logger, "SavePlayerSettings")
                                 .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to save settings for {SteamId}", p.SteamId);
            }
        });

        if (wait)
        {
            save.Wait(TimeSpan.FromSeconds(2));
        }
    }

    // ------------------------------------------------------------------ layout entity

    private static ICustomHudLayout? GetLayout(HudPlayer p)
        => p.Layout is { } layout && layout.IsValid() ? layout : null;

    /// <summary>
    ///     The player's own layout entity, spawned when missing: on joining, after a map change, or if anything
    ///     removed it. Only its player receives it, and a fresh entity holds none of the HUD's values yet.
    /// </summary>
    private ICustomHudLayout? EnsureLayout(HudPlayer p, IPlayerController controller, float now)
    {
        if (GetLayout(p) is { } existing)
        {
            return existing;
        }

        if (now < p.NextLayoutAttempt && p.NextLayoutAttempt - now <= LayoutRetryInterval)
        {
            return null;
        }

        p.NextLayoutAttempt = now + LayoutRetryInterval;

        if (_panorama.CreateLayout(timer_hud_layout.GetString(), $"timer_hud_{p.Slot}") is not { } layout)
        {
            _logger.LogWarning("Failed to spawn the HUD layout for slot {slot}", p.Slot);

            return null;
        }

        // Hidden from everyone, then shown to its player.
        _transmit.AddEntityHooks(layout, false);
        _transmit.SetEntityState(layout.Index, controller.Index, true, -1);

        p.Layout    = layout;
        p.MenuDirty = true; // a new entity holds none of it yet
        p.ForgetSent();

        if (p.AnyMenuOpen && p.Drag is null)
        {
            layout.SetInputCaptureEnabled(p.Slot, true);
        }

        return layout;
    }

    /// <summary>
    ///     Removes the entities the HUD spawned for the player: their layout, and the drag camera. A camera that held
    ///     the view until now is only switched off, and goes with the map: removing it now would leave the client
    ///     looking through it.
    /// </summary>
    private void RemoveEntities(HudPlayer? p)
    {
        if (p is null)
        {
            return;
        }

        var frozen = p.Drag is { Frozen: true };

        if (p.Drag is { } drag)
        {
            UnfreezeView(p, drag);
        }

        if (p.Camera is { } camera && camera.IsValid() && !frozen)
        {
            camera.Kill();
        }

        p.Camera = null;

        if (GetLayout(p) is { } layout)
        {
            layout.SetInputCaptureEnabled(p.Slot, false);
            layout.Kill();
        }

        p.Layout = null;
        p.ForgetSent();
    }

    // ------------------------------------------------------------------ refresh

    private void OnGameFramePost(bool simulating, bool firstTick, bool lastTick)
    {
        var gameRules = _bridge.GameRules;

        if (!gameRules.IsWarmupPeriod)
        {
            gameRules.IsGameRestart = gameRules.RestartRoundTime < _bridge.GlobalVars.CurTime;
        }

        var now = _bridge.GlobalVars.CurTime;
        SyncRoundTime(gameRules, now);
        RebuildSpectators(now);

        foreach (var p in _players)
        {
            if (p is null)
            {
                continue;
            }

            // CurTime starts over on a map change, so a time further ahead than it could be is stale.
            if (!float.IsNaN(p.SaveAt) && (now >= p.SaveAt || p.SaveAt - now > SaveDelay))
            {
                FlushSettings(p);
            }

            if (now < p.NextHudAt && p.NextHudAt - now <= HudUpdateInterval)
            {
                continue;
            }

            p.NextHudAt = now + HudUpdateInterval;
            Refresh(p, now);
        }
    }

    private void Refresh(HudPlayer p, float now)
    {
        // Only for a fully connected player, like ModSharp's transmit example: the data is ready by then.
        if (!_bridge.TryGetController(p.Slot, out var controller)
            || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
        {
            return;
        }

        // A dead pawn stops running commands, so the run-command hook can't end its drag (and free the view): here.
        if (p.Drag is not null && controller.GetPlayerPawn() is not { IsAlive: true })
        {
            EndDrag(p, DragEnd.Cancel); // refreshes

            return;
        }

        TrackRun(p);

        if (EnsureLayout(p, controller, now) is { } layout)
        {
            UpdateHud(p, layout, controller, now);
        }
    }

    /// <summary>
    ///     Applies a click or command straight away rather than on the next refresh.
    /// </summary>
    private void RefreshNow(HudPlayer p)
        => Refresh(p, _bridge.GlobalVars.CurTime);

    /// <summary>
    ///     The timer stops silently (noclip, !stop, a skipped stage or checkpoint...), so a run that stopped short
    ///     of the end is noticed here: stopped without a finish, and not by going back to the start.
    /// </summary>
    private void TrackRun(HudPlayer p)
    {
        if (_timerModule.GetTimerInfo(p.Slot) is not { } info)
        {
            return;
        }

        var status = info.Status;

        if (p.LastStatus != ETimerStatus.Stopped
            && status == ETimerStatus.Stopped
            && p.Finish is null
            && p.ZoneType != EZoneType.Start)
        {
            p.Stopped = true;
        }

        p.LastStatus = status;
    }

    // While the HUD settings are open the command carries no movement (the movement itself is blocked in
    // OnPlayerProcessMovePre). While a drag holds the player's view still, their client still turns the view it sends:
    // that moves the panel, and is then taken back out of the command, so the pawn keeps facing where it did and
    // spectators don't see it spin.
    private unsafe HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      param,
                                                                         HookReturnValue<EmptyHookReturn> ret)
    {
        if (_players[param.Client.Slot] is not { MovementLocked: true } p)
        {
            return new ();
        }

        var cmd = param.BaseUserCmd;

        if (cmd == null)
        {
            return new ();
        }

        cmd->ForwardMove = 0;
        cmd->SideMove    = 0;
        cmd->UpMove      = 0;

        if (p.Drag is not { } drag || cmd->ViewAngles == null)
        {
            return new ();
        }

        drag.Aim = cmd->ViewAngles->Value;

        if (drag.Frozen)
        {
            cmd->ViewAngles->Value = drag.View;
        }

        return new ();
    }

    // The keys that move the player, which do nothing while they edit the HUD. Duck stays, so opening the menu doesn't
    // stand them up.
    private const UserCommandButtons MoveKeys = UserCommandButtons.Forward
                                                | UserCommandButtons.Back
                                                | UserCommandButtons.MoveLeft
                                                | UserCommandButtons.MoveRight
                                                | UserCommandButtons.Jump;

    // With the HUD settings open (dragging included), the player's keys don't move them, the way StyleModule blocks a
    // style's keys. Momentum and gravity still apply.
    private unsafe void OnPlayerProcessMovePre(IPlayerProcessMoveForwardParams param)
    {
        if (param.Client.IsFakeClient || _players[param.Client.Slot] is not { MovementLocked: true })
        {
            return;
        }

        var mv = param.Info;
        mv->ForwardMove = 0;
        mv->SideMove    = 0;
        mv->UpMove      = 0;

        param.Service.KeyButtons &= ~MoveKeys;
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var slot = param.Client.Slot;
        var pawn = param.Pawn;
        var now  = _bridge.GlobalVars.CurTime;

        TrackTurn(slot, pawn.GetEyeAngles().Y, now);

        if (param.Client.IsFakeClient || _players[slot] is not { } p)
        {
            return;
        }

        if (pawn.AsObserver() is { } observer)
        {
            // Spectating the central replay bot, E opens the replay menu.
            if ((param.KeyButtons & param.ChangedButtons & UserCommandButtons.Use) != 0
                && _central.CentralBot is { } central
                && ObservedSlot(observer) == central.Slot)
            {
                SetReplayMenuOpen(p, !p.Replays.Open);
                RefreshNow(p);
            }

            EndDrag(p, DragEnd.Cancel);

            return;
        }

        // Walk + inspect shows or hides the saved-locations panel; inspecting on its own doesn't.
        if ((param.KeyButtons & UserCommandButtons.Speed) != 0
            && (param.KeyButtons & param.ChangedButtons & UserCommandButtons.LookAtWeapon) != 0)
        {
            p.LocsShown = !p.LocsShown;
            RefreshNow(p);
        }

        if (p.Drag is not { } drag)
        {
            return;
        }

        if (!pawn.IsAlive)
        {
            EndDrag(p, DragEnd.Cancel);

            return;
        }

        TickDrag(p, drag, param.KeyButtons, param.ChangedButtons, now);
    }

    private void TrackTurn(PlayerSlot slot, float yaw, float now)
    {
        var turned = AngleDelta(yaw, _keyYaw[slot]);
        _keyYaw[slot] = yaw;

        if (MathF.Abs(turned) > 0.05f)
        {
            _turn[slot]   = turned > 0 ? 1 : -1;
            _turnAt[slot] = now;
        }
        else if (now - _turnAt[slot] > 0.1f || now < _turnAt[slot])
        {
            _turn[slot] = 0;
        }
    }

    /// <summary>
    ///     Signed difference a - b in degrees, in [-180, 180).
    /// </summary>
    private static float AngleDelta(float a, float b)
        => ((a - b + 540f) % 360f) - 180f;

    // ------------------------------------------------------------------ commands and clicks

    private ECommandAction OnCommandHud(PlayerSlot slot, StringCommand command)
    {
        if (_players[slot] is { } p)
        {
            SetMenuOpen(p, !p.MenuOpen);
            RefreshNow(p);
        }

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandShowKeys(PlayerSlot slot, StringCommand command)
    {
        if (_players[slot] is { } p)
        {
            p.Settings[HudOptions.Keys.Index] = p.IsOn(HudOptions.Keys) ? 1 : 0;
            p.MenuDirty = true;
            MarkSettingsChanged(p);
            RefreshNow(p);
        }

        return ECommandAction.Handled;
    }

    private void SetMenuOpen(HudPlayer p, bool open)
    {
        p.MenuOpen  = open;
        p.MenuDirty = true;

        if (open)
        {
            p.Replays.Open = false;
            p.Profile.Open = false;
            CloseNominateMenu(p);
            CloseZonePanel(p);
            CloseRecords(p);
            CloseStyles(p);
            CloseMapInfo(p);
        }
        else
        {
            EndDrag(p, DragEnd.Cancel);
        }

        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void OnHudClicked(IPlayerController player, ICustomHudLayout layout, string buttonId)
    {
        if (_players[player.PlayerSlot] is not { } p || !layout.Equals(p.Layout))
        {
            return;
        }

        if (buttonId.StartsWith("Rm", StringComparison.Ordinal))
        {
            ClickReplayMenu(p, buttonId);
        }
        else if (buttonId.StartsWith("Mc", StringComparison.Ordinal))
        {
            ClickMapChooser(p, buttonId);
        }
        else if (buttonId.StartsWith("Zn", StringComparison.Ordinal))
        {
            ClickZonePanel(p, buttonId);
        }
        else if (buttonId.StartsWith("Lb", StringComparison.Ordinal))
        {
            ClickRecords(p, buttonId);
        }
        else if (buttonId.StartsWith("Sty", StringComparison.Ordinal))
        {
            ClickStyles(p, buttonId);
        }
        else if (buttonId.StartsWith("Mi", StringComparison.Ordinal))
        {
            ClickMapInfo(p, buttonId);
        }
        else if (buttonId.StartsWith("Pf", StringComparison.Ordinal))
        {
            ClickProfile(p, buttonId);
        }
        else if (Array.IndexOf(HudTabs.Tabs, buttonId) is var tab and >= 0)
        {
            p.Tab = tab;
        }
        else if (TryParseStep(buttonId, out var stepped, out var delta))
        {
            StepOption(p, stepped, delta);
        }
        else if (TryParseRow(buttonId, out var row, out var action))
        {
            ClickRow(p, row, action);
        }
        else if (HudOptions.ById.TryGetValue(buttonId, out var option))
        {
            // Greyed-out options ignore clicks; steppers only change through their buttons.
            if ((option.Needs is null || option.Needs(p.Settings)) && !option.IsStepper)
            {
                p.Settings[option.Index] = (p.Settings[option.Index] + 1) % option.Choices.Length;
                MarkSettingsChanged(p);
            }
        }
        else if (buttonId == "MenuReset")
        {
            // The HUD's reset: hide and sounds stay as they are.
            var hide   = p.Settings[HudOptions.Hide.Index];
            var sounds = p.Settings[HudOptions.Sounds.Index];
            p.ResetSettings();
            p.Settings[HudOptions.Hide.Index]   = hide;
            p.Settings[HudOptions.Sounds.Index] = sounds;
            Array.Fill(p.UnplaceAt, float.NaN);
            MarkSettingsChanged(p);
        }
        else if (buttonId == "MenuClose")
        {
            SetMenuOpen(p, false);
        }
        else if (p.MenuOpen
                 && HudTargets.Buttons.TryGetValue(buttonId, out var target)
                 && player.GetPlayerPawn() is { IsAlive: true } pawn)
        {
            StartDrag(p, pawn, target);
        }

        p.MenuDirty = true;
        RefreshNow(p);
    }

    private static bool TryParseStep(string buttonId, out HudOption option, out int delta)
    {
        foreach (var candidate in HudOptions.All)
        {
            if (!candidate.IsStepper)
            {
                continue;
            }

            if (buttonId == candidate.DownId || buttonId == candidate.UpId)
            {
                option = candidate;
                delta  = buttonId == candidate.UpId ? 1 : -1;

                return true;
            }
        }

        option = null!;
        delta  = 0;

        return false;
    }

    private void StepOption(HudPlayer p, HudOption option, int delta)
    {
        var index = p.Settings[option.Index] + delta;

        if (index >= 0 && index < option.Choices.Length)
        {
            p.Settings[option.Index] = index;
            MarkSettingsChanged(p);
        }
    }

    private enum RowAction
    {
        Up,
        Down,
        Toggle,
    }

    /// <summary>
    ///     Row&lt;i&gt;Up / Row&lt;i&gt;Down / Row&lt;i&gt;Toggle on the Timer tab.
    /// </summary>
    private static bool TryParseRow(string buttonId, out int row, out RowAction action)
    {
        row    = -1;
        action = RowAction.Toggle;

        if (buttonId.Length < 6 || !buttonId.StartsWith("Row", StringComparison.Ordinal) || !char.IsAsciiDigit(buttonId[3]))
        {
            return false;
        }

        row = buttonId[3] - '0';

        switch (buttonId.AsSpan(4))
        {
            case "Up":
                action = RowAction.Up;

                break;
            case "Down":
                action = RowAction.Down;

                break;
            case "Toggle":
                action = RowAction.Toggle;

                break;
            default:
                return false;
        }

        return row < HudLines.Count;
    }

    /// <summary>
    ///     Timer tab row i: move the line in slot i, or switch it (the blank line has no switch).
    /// </summary>
    private void ClickRow(HudPlayer p, int row, RowAction action)
    {
        if (action == RowAction.Toggle)
        {
            if (HudLines.Option(p.Order[row]) is { } option)
            {
                p.Settings[option.Index] = (p.Settings[option.Index] + 1) % option.Choices.Length;
                MarkSettingsChanged(p);
            }

            return;
        }

        var to = row + (action == RowAction.Up ? -1 : 1);

        if (to < 0 || to >= HudLines.Count)
        {
            return;
        }

        (p.Order[row], p.Order[to]) = (p.Order[to], p.Order[row]);
        MarkSettingsChanged(p);
    }

    // ------------------------------------------------------------------ run events

    public void OnZoneStartTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (_players[controller.PlayerSlot] is not { } p
            || info.ZoneType is not (EZoneType.Start or EZoneType.Stage or EZoneType.End))
        {
            return;
        }

        p.ZoneType  = info.ZoneType;
        p.ZoneTrack = info.Track;
        p.ZoneData  = info.Data;

        // Back at the start: a new attempt.
        if (info.ZoneType == EZoneType.Start)
        {
            p.ClearRun();
        }
    }

    public void OnZoneEndTouch(IZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (_players[controller.PlayerSlot] is { } p
            && p.ZoneType == info.ZoneType
            && p.ZoneTrack == info.Track
            && p.ZoneData == info.Data)
        {
            p.ZoneType = EZoneType.Invalid;
        }
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (_players[controller.PlayerSlot] is not { } p)
        {
            return;
        }

        p.ClearRun();
        FetchPbCheckpoints(p, timerInfo.Style, timerInfo.Track);
    }

    // Read the records here: saving the run can replace them right after.
    public void OnPlayerStageTimerFinish(IPlayerController controller, IPlayerPawn pawn, IStageTimerInfo stageTimerInfo)
    {
        if (_players[controller.PlayerSlot] is not { } p)
        {
            return;
        }

        var stage = stageTimerInfo.Stage;
        var style = stageTimerInfo.Style;
        var track = stageTimerInfo.Track;

        if (stage is < 1 or >= TimerConstants.MAX_STAGE)
        {
            return;
        }

        var pb = _recordModule.GetPlayerRecord(p.Slot, style, track, stage)?.Time;
        var wr = _recordModule.GetWR(style, track, stage)?.Time;

        // The run's time here, and the server record's from its replay's stage marks.
        var cum   = _timerModule.GetTimerInfo(p.Slot)?.Time ?? stageTimerInfo.Time;
        var cumWr = RecordStageSplit(style, track, stage);

        p.LastStage = new HudStageResult(stage, stageTimerInfo.Time, pb, wr);
        p.AddSplit(new HudSplit(true, stage, stageTimerInfo.Time, pb, wr, cum, null, cumWr));
    }

    public void OnReachCheckpoint(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo, int checkpoint)
    {
        if (_players[controller.PlayerSlot] is not { } p || checkpoint < 1 || checkpoint > timerInfo.Checkpoints.Count)
        {
            return;
        }

        var time = timerInfo.Checkpoints[checkpoint - 1].Time;
        var wr   = _recordModule.GetWRCheckpoints(timerInfo.Style, timerInfo.Track) is { } wrs && wrs.Count >= checkpoint
            ? wrs[checkpoint - 1].Time
            : (float?) null;

        var pbs = p.PbCheckpoints;
        var pb = pbs.Style == timerInfo.Style && pbs.Track == timerInfo.Track && pbs.Checkpoints is { } list
                 && list.Count >= checkpoint
            ? list[checkpoint - 1].Time
            : (float?) null;

        p.AddSplit(new HudSplit(false, checkpoint, time, pb, wr, time, pb, wr));
    }

    public void OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        if (_players[controller.PlayerSlot] is not { } p)
        {
            return;
        }

        var style = timerInfo.Style;
        var track = timerInfo.Track;
        var pb    = _recordModule.GetPlayerRecord(p.Slot, style, track);
        var wr    = _recordModule.GetWR(style, track);

        // The last stage's own time, on staged maps.
        var stage = p.LastStage is { } last && last.Stage == _zoneModule.GetTotalStages(track) ? last : null;

        p.Finish = new HudFinish(track,
                                 timerInfo.Time,
                                 pb?.Time,
                                 wr?.Time,
                                 stage,
                                 HudFormat.RoundSpeed(timerInfo.EndVelocity.Length2D()),
                                 pb is null ? null : HudFormat.RoundSpeed(Length2D(pb.VelocityEndX, pb.VelocityEndY)),
                                 wr is null ? null : HudFormat.RoundSpeed(Length2D(wr.VelocityEndX, wr.VelocityEndY)),
                                 _practiceModule.IsInPractice(p.Slot));

        p.Stopped = false;
    }

    private static float Length2D(float x, float y)
        => MathF.Sqrt((x * x) + (y * y));

    /// <summary>
    ///     The server record's run time at the end of a stage, from its replay's stage marks; null without a replay.
    /// </summary>
    private float? RecordStageSplit(int style, int track, int stage)
    {
        if (_replayModule.GetCachedReplay(style, track, 0) is not { Header: { StageTicks: { } ticks } header }
            || stage > ticks.Count)
        {
            return null;
        }

        var frames = ticks[stage - 1] - header.PreFrame;

        return frames > 0 ? frames * TimerConstants.TickInterval : null;
    }

    /// <summary>
    ///     The record cache keeps the server record's checkpoint splits but not a player's own, so those are
    ///     fetched per style and track, like !cpr does.
    /// </summary>
    private void FetchPbCheckpoints(HudPlayer p, int style, int track)
    {
        if (!_zoneModule.CurrentTrackHasCheckpoints(track))
        {
            return;
        }

        if (_recordModule.GetPlayerRecord(p.Slot, style, track) is not { } pb)
        {
            p.PbCheckpoints = (style, track, 0, null);

            return;
        }

        if (p.PbCheckpoints.Style == style && p.PbCheckpoints.Track == track && p.PbCheckpoints.RecordId == pb.Id)
        {
            return;
        }

        p.PbCheckpoints = (style, track, pb.Id, null);
        _               = LoadPbCheckpointsAsync(p, style, track, pb.Id);
    }

    private async Task LoadPbCheckpointsAsync(HudPlayer p, int style, int track, long recordId)
    {
        try
        {
            var checkpoints = await _request.GetRecordCheckpoints(recordId).ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_players[p.Slot] == p && p.PbCheckpoints == (style, track, recordId, null))
                {
                    p.PbCheckpoints = (style, track, recordId, checkpoints);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to load PB checkpoints of record {recordId} for the HUD", recordId);
        }
    }
}

