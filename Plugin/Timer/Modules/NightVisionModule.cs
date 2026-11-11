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
using System.Numerics;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;

namespace Source2Surf.Timer.Modules;

internal interface INightVisionModule
{
}

/// <summary>
///     !nv: night vision, the map held at an exposure the player picks; a player setting. A player who has it on gets an
///     env_tonemap_controller2 that only they receive, and their pawns' cameras point at it (m_hTonemapController). The
///     client takes a camera's tonemap controller over any post-processing volume's exposure, so it holds inside the
///     map's volumes too, and their grading and bloom stay. Spectating in first or third person, the client looks at the
///     target's controller as well, which nobody else receives, so every player sees by their own setting. Each camera's
///     PostThink points it back at the map's master controller (or none) every tick, so it's pointed again each frame.
/// </summary>
internal class NightVisionModule : IModule, INightVisionModule, IPlayerManagerListener
{
    private const string TonemapField       = "m_hTonemapController";
    private const float  SpawnRetryInterval = 5f; // after the controller failed to spawn

    private static readonly Dictionary<string, KeyValuesVariantValueItem> NoKeyValues = [];

    private readonly InterfaceBridge            _bridge;
    private readonly ITransmitManager           _transmit;
    private readonly ICommandManager            _commandManager;
    private readonly IPlayerManager             _playerManager;
    private readonly ILocalizationProvider      _localization;
    private readonly IPlayerSettings            _settings;
    private readonly ILogger<NightVisionModule> _logger;

    // By player slot: who has night vision on, their controller, and when a controller that failed to spawn is tried again.
    private          ulong          _on;
    private readonly IBaseEntity?[] _tonemaps = new IBaseEntity?[PlayerSlot.MaxPlayerCount];
    private readonly float[]        _retryAt  = new float[PlayerSlot.MaxPlayerCount];

    public NightVisionModule(InterfaceBridge            bridge,
                             ISharedSystem              shared,
                             ICommandManager            commandManager,
                             IPlayerManager             playerManager,
                             ILocalizationProvider      localization,
                             IPlayerSettings            settings,
                             ILogger<NightVisionModule> logger)
    {
        _bridge         = bridge;
        _transmit       = shared.GetTransmitManager();
        _commandManager = commandManager;
        _playerManager  = playerManager;
        _localization   = localization;
        _settings       = settings;
        _logger         = logger;
    }

    public bool Init()
    {
        _playerManager.RegisterListener(this);
        _settings.Changed += OnSettingsChanged;
        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);

        _commandManager.AddClientChatCommand("nv", OnCommandNightVision);
        _commandManager.AddClientChatCommand("nightvision", OnCommandNightVision);

        return true;
    }

    public void Shutdown()
    {
        _playerManager.UnregisterListener(this);
        _settings.Changed -= OnSettingsChanged;
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);

        // Nothing would point the cameras again once this is unloaded.
        _on = 0;

        for (var slot = 0; slot < _tonemaps.Length; slot++)
        {
            Remove((byte) slot);
        }
    }

    // ------------------------------------------------------------------ players

    public void OnClientPutInServer(PlayerSlot slot)
    {
        _retryAt[slot] = 0;
        OnSettingsChanged(slot);
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        _on &= ~Bit(slot);
        Remove(slot);
    }

    private void OnSettingsChanged(PlayerSlot slot)
    {
        if (!_settings.UsesNightVision(slot))
        {
            _on &= ~Bit(slot);
            Remove(slot);

            return;
        }

        _on |= Bit(slot);

        if (Live(slot) is { } tonemap)
        {
            SetExposure(tonemap, Exposure(slot));
        }
    }

    // !nv switches night vision; !nv <1-10> sets its brightness and turns it on; !nv 0 turns it off.
    private ECommandAction OnCommandNightVision(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var tr  = _localization.For(slot);
        var arg = command.ArgString.Trim();
        var on  = !_settings.UsesNightVision(slot);

        if (arg.Length > 0)
        {
            if (ReadBrightness(arg) is not { } brightness)
            {
                controller.PrintToChat(tr.Format(ChatTexts.NightVisionUsage,
                                                 HudOptions.MinNightVision / 10,
                                                 HudOptions.MaxNightVision / 10));

                return ECommandAction.Handled;
            }

            on = brightness != 0;

            if (on)
            {
                _settings.SetNightVisionBrightness(slot, brightness);
            }
        }

        _settings.SetUsesNightVision(slot, on);
        OnSettingsChanged(slot);

        controller.PrintToChat(on
                                   ? tr.Format(ChatTexts.NightVisionOn, Exposure(slot).ToString("0.#", CultureInfo.InvariantCulture))
                                   : tr[ChatTexts.NightVisionOff]);

        return ECommandAction.Handled;
    }

    /// <summary>
    ///     !nv's argument: a brightness in tenths, 0 to turn night vision off, or null for anything else.
    /// </summary>
    internal static int? ReadBrightness(string arg)
    {
        if (!float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        if (value == 0)
        {
            return 0;
        }

        // NaN fails both.
        if (!(value >= HudOptions.MinNightVision / 10f && value <= HudOptions.MaxNightVision / 10f))
        {
            return null;
        }

        return (int) MathF.Round(value * 10f);
    }

    private float Exposure(PlayerSlot slot)
        => Math.Clamp(_settings.NightVisionBrightness(slot), HudOptions.MinNightVision, HudOptions.MaxNightVision) / 10f;

    // Equal ends hold the exposure there rather than letting it adapt to what's on screen.
    private static void SetExposure(IBaseEntity tonemap, float exposure)
    {
        tonemap.SetNetVar("m_flAutoExposureMin", exposure);
        tonemap.SetNetVar("m_flAutoExposureMax", exposure);
    }

    // ------------------------------------------------------------------ cameras

    private void OnGameFramePost(bool simulating, bool firstTick, bool lastTick)
    {
        for (var bits = _on; bits != 0; bits &= bits - 1)
        {
            Apply((byte) BitOperations.TrailingZeroCount(bits));
        }
    }

    // Both pawns: the observer's camera is the one the client uses while dead or spectating.
    private void Apply(PlayerSlot slot)
    {
        if (!_bridge.TryGetController(slot, out var controller)
            || controller.ConnectedState != PlayerConnectedState.PlayerConnected
            || Ensure(slot, controller) is not { } tonemap)
        {
            return;
        }

        uint handle = tonemap.RefHandle;

        Point(controller.GetPlayerPawn(), handle);
        Point(controller.GetObserverPawn(), handle);
    }

    // Written only when it changed, which is after the camera's PostThink pointed it elsewhere.
    private static void Point(IBasePlayerPawn? pawn, uint handle)
    {
        if (pawn?.GetCameraService() is { } camera)
        {
            camera.SetNetVar(TonemapField, handle);
        }
    }

    private IBaseEntity? Live(PlayerSlot slot)
        => _tonemaps[slot] is { } tonemap && tonemap.IsValid() ? tonemap : null;

    /// <summary>
    ///     The player's controller, spawned when missing: once they turn night vision on, after a map change, or if
    ///     anything removed it.
    /// </summary>
    private IBaseEntity? Ensure(PlayerSlot slot, IPlayerController controller)
    {
        if (Live(slot) is { } live)
        {
            return live;
        }

        var now = _bridge.GlobalVars.CurTime;

        // The time goes back to 0 on a map change.
        if (now < _retryAt[slot] && _retryAt[slot] - now <= SpawnRetryInterval)
        {
            return null;
        }

        if (_bridge.EntityManager.SpawnEntitySync<IBaseEntity>("env_tonemap_controller2", NoKeyValues) is not { IsValidEntity: true } tonemap)
        {
            _retryAt[slot] = now + SpawnRetryInterval;
            _logger.LogWarning("Failed to spawn the night vision tonemap controller for slot {slot}", slot);

            return null;
        }

        // Hidden from everyone, then shown to its player.
        _transmit.AddEntityHooks(tonemap, false);
        _transmit.SetEntityState(tonemap.Index, controller.Index, true, -1);
        SetExposure(tonemap, Exposure(slot));

        _tonemaps[slot] = tonemap;

        return tonemap;
    }

    // Points the player's cameras away from their controller, then removes it.
    private void Remove(PlayerSlot slot)
    {
        if (_tonemaps[slot] is not { } tonemap)
        {
            return;
        }

        _tonemaps[slot] = null;

        if (!tonemap.IsValid())
        {
            return;
        }

        uint handle = tonemap.RefHandle;

        if (_bridge.TryGetController(slot, out var controller))
        {
            Release(controller.GetPlayerPawn(), handle);
            Release(controller.GetObserverPawn(), handle);
        }

        tonemap.Kill();
    }

    // The camera's next PostThink points it at the map's master controller, if the map has one.
    private static void Release(IBasePlayerPawn? pawn, uint handle)
    {
        if (pawn?.GetCameraService() is { } camera && camera.GetNetVar<uint>(TonemapField) == handle)
        {
            camera.SetNetVar(TonemapField, uint.MaxValue);
        }
    }

    private static ulong Bit(int slot)
        => 1UL << slot;
}
