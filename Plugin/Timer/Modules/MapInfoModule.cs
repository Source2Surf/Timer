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
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Utilities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

internal interface IMapInfoModule
{
    float GetEnterSpeedLimit(int track);

    int? GetZoneMaxJumpsOverride(int track);

    int GetGameModeMaxPrejumps();

    bool RequiresCheckpoints(int track);

    EGameMode GetCurrentGameMode();

    float? GetZoneExitSpeedOverride(int track);

    float GetGameModeExitSpeedLimit();

    float GetStageEnterSpeedLimit(int track);

    float? GetStageExitSpeedOverride(int track);

    float GetDefaultAirAccelerate();

    float GetGameModeWishSpeed();

    MapProfile GetCurrentMapProfile();
}

internal class MapInfoModule : IModule, IMapInfoModule, IGameListener
{
    private readonly InterfaceBridge _bridge;
    private readonly IRequestManager _requestManager;
    private readonly ICommandManager _commandManager;
    private readonly ILocalizationProvider _localization;

    private readonly ILogger<MapInfoModule>          _logger;
    private readonly TaskTracker                     _taskTracker;

    // Placeholder until the async GetMapInfo load completes (never null): consumers
    // (!tier, !mi, replay StorePendingReplay) may run before/without a DB round-trip.
    private MapProfile _currentMapProfileInfo = new () { MapName = string.Empty };

    private double     _currentMapStartTime;
    private MapConfig? _currentMapConfig = null;

    private readonly string _configPath;
    private readonly string _gameModesPath;

    private readonly string[] _baseCvars =
    [
        "bot_quota 0",
        "bot_quota_mode normal",
        "mp_limitteams 0",
        "bot_chatter off",
        "bot_flipout 1",
        "bot_zombie 1",
        "bot_stop 1",
        "mp_autoteambalance 0",
        "bot_controllable 0",
        "mp_ignore_round_win_conditions 1",
        "sv_accelerate 5",
        "sv_friction 4",
        "sv_jump_precision_enable 0",
        "sv_staminajumpcost 0",
        "sv_staminalandcost 0",
        "sv_disable_radar 1",
        "sv_subtick_movement_view_angles 0",
        "mp_solid_enemies 0",
        "mp_solid_teammates 0",
        "sv_legacy_jump 1",
        "bot_auto_vacate 0",
        "ms_override_team_limit 1",
    ];

    // After every config: MiscModule skips the GameCommencing round restart, and warmup then starts without it, leaving
    // clients' warmup panel up until they rejoin.
    private static readonly string[] ForcedCvars =
    [
        "mp_warmup_online_enabled 0",
        "mp_warmup_offline_enabled 0",
    ];

    private EGameMode _currentGameMode = EGameMode.None;

    private GameModeConfig _currentGameModeConfig = new ();

    private bool _mapStatsPersisted;

    private readonly IConVar? _maxVelocity;
    private          string?  _maxVelocityBefore;

    // Late-resolved to avoid circular DI (RecordModule depends on IMapInfoModule)
    private IRecordModule _recordModule = null!;
    private IZoneModule   _zoneModule   = null!;

    public MapInfoModule(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ICommandManager        commandManager,
                         ILocalizationProvider  localization,
                         ILogger<MapInfoModule> logger)
    {
        _bridge         = bridge;
        _requestManager = requestManager;
        _commandManager = commandManager;
        _localization   = localization;
        _logger         = logger;
        _taskTracker    = new TaskTracker(logger);
        _maxVelocity    = bridge.ConVarManager.FindConVar("sv_maxvelocity");

        _configPath    = Path.Combine(bridge.TimerDataPath, "map_configs");
        _gameModesPath = Path.Combine(bridge.SharpPath, "configs", "timer-gamemodes.jsonc");

        if (!Directory.Exists(_configPath))
        {
            Directory.CreateDirectory(_configPath);
        }
    }

    public bool Init()
    {
        // No map is loaded yet: ModSharp loads modules at server boot and reloads them only
        // during a map change, so OnGameInit always follows and fills in the map name.
        _currentMapProfileInfo = new () { MapName = string.Empty };

        _bridge.ModSharp.InstallGameListener(this);

        _commandManager.AddAdminChatCommand("set_tier", ["timer:tier"], OnCommandSetTier);
        _commandManager.AddClientChatCommand("mi", OnCommandMapInfo);
        _commandManager.AddClientChatCommand("mapinfo", OnCommandMapInfo);
        _commandManager.AddClientChatCommand("tier", OnCommandTier);
        _commandManager.AddClientChatCommand("playtime", OnCommandPlaytime);

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _recordModule = provider.GetRequiredService<IRecordModule>();
        _zoneModule   = provider.GetRequiredService<IZoneModule>();
    }

    public void Shutdown()
    {
        PersistCurrentMapStats();
        _taskTracker.DrainPendingTasks();

        _bridge.ModSharp.RemoveGameListener(this);
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void OnGameInit()
    {
        _bridge.RefreshMapName();
        _currentMapStartTime = _bridge.ModSharp.EngineTime();
        _mapStatsPersisted   = false;

        // Reset immediately: keeping the previous map's profile visible during the async
        // load would let consumers key data (e.g. replays) against the WRONG MapId.
        _currentMapProfileInfo = new () { MapName = _bridge.CurrentMapName };

        BindWorkshopItem(_bridge.CurrentMapName);

        var loadTask = Task.Run(async () =>
        {
            try
            {
                var profile = await RetryHelper.RetryAsync(
                    () => _requestManager.GetMapInfo(_bridge.CurrentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapInfo"
                ).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() => _currentMapProfileInfo = profile);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to get map profile");
            }
        }, _bridge.CancellationToken);

        _taskTracker.Track(loadTask);
    }

    // Must run before any module's first request for this map.
    private void BindWorkshopItem(string mapName)
    {
        try
        {
            var workshopId = WorkshopMaps.FindItemId(mapName,
                                                     _bridge.ModSharp.ListWorkshopMaps(),
                                                     _bridge.ModSharp.GetAddonName());

            _requestManager.SetMapWorkshopId(mapName, workshopId);
            _logger.LogInformation("Map {map} workshop item: {workshopId}", mapName, workshopId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when binding map {map} to its workshop item", mapName);
        }
    }

    public void OnGameActivate()
    {
        LoadGameModeConfig();
        LoadMapConfig();
        ApplyMaxVelocity();

        foreach (var cvar in ForcedCvars)
        {
            _bridge.ModSharp.ServerCommand(cvar);
        }
    }

    // A map's max_velocity only lasts the map: the next map without one gets back what was there before.
    private void ApplyMaxVelocity()
    {
        if (_maxVelocity is null)
        {
            return;
        }

        if (_currentMapConfig?.MaxVelocity is { } value)
        {
            _maxVelocityBefore ??= _maxVelocity.GetString();
            _bridge.ModSharp.ServerCommand($"sv_maxvelocity {value.ToString(CultureInfo.InvariantCulture)}");
        }
        else if (_maxVelocityBefore is { } before)
        {
            _bridge.ModSharp.ServerCommand($"sv_maxvelocity {before}");
            _maxVelocityBefore = null;
        }
    }

    public void OnGamePreShutdown()
    {
        _currentMapConfig = null;
        PersistCurrentMapStats();
    }

    private ECommandAction OnCommandSetTier(PlayerSlot slot, StringCommand command)
    {
        if (command.ArgCount < 1 || !command.TryGetArg<byte>(1, out var tier) || tier == 0)
        {
            return ECommandAction.Handled;
        }

        var mapName = _bridge.CurrentMapName;

        _taskTracker.Track(Task.Run(async () =>
        {
            try
            {
                var result = await RetryHelper.RetryAsync(() => _requestManager.SetMapTierAsync(mapName, tier),
                                                          RetryHelper.IsTransient, _logger, "SetMapTierAsync")
                                              .ConfigureAwait(false);

                if (!result.MapFound)
                {
                    _logger.LogWarning("set_tier: map {map} isn't stored yet.", mapName);

                    return;
                }

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (string.Equals(_currentMapProfileInfo.MapName, mapName, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentMapProfileInfo.Tier[0] = tier;
                    }
                }).ConfigureAwait(false);

                _logger.LogInformation("Set {map}'s tier to {tier}; queued {boards} score board(s) for recalculation.",
                                       mapName, tier, result.BoardsQueued);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when setting the tier of {map}", mapName);
            }
        }, _bridge.CancellationToken));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandTier(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        controller.PrintToChat(_localization.For(slot).Format(ChatTexts.MapTier, _bridge.CurrentMapName, Utils.Highlight(_currentMapProfileInfo.Tier[0])));

        return ECommandAction.Handled;
    }

    private ECommandAction OnCommandPlaytime(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetClientController(slot, out var client, out _))
        {
            return ECommandAction.Handled;
        }

        var tr             = _localization.For(slot);
        var steamId        = client.SteamId;
        var mapName        = _bridge.CurrentMapName;
        var currentSession = _recordModule.GetSessionTime(slot);

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetPlayerMapStatsAsync",
                             () => _requestManager.GetPlayerMapStatsAsync(steamId, mapName),
                             (ctrl, stats) =>
                             {
                                 var (dbPlayTime, playCount) = stats;
                                 var totalTime               = dbPlayTime + currentSession;

                                 ctrl.PrintToChat(tr.Format(ChatTexts.MapPlaytime,
                                                            Utils.Highlight(mapName),
                                                            Utils.Highlight(Playtime(tr, totalTime)),
                                                            Utils.Highlight(playCount + 1)));
                             });

        return ECommandAction.Handled;
    }

    private static string Date(ChatTr tr, long unixMilliseconds)
        => unixMilliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : tr[ChatTexts.MapDateUnknown];

    private static string Playtime(ChatTr tr, float totalSeconds)
    {
        var hours   = (int) (totalSeconds / 3600f);
        var minutes = (int) ((totalSeconds % 3600f) / 60f);

        return hours > 0 ? tr.Format(ChatTexts.Hours, hours, minutes) : tr.Format(ChatTexts.Minutes, minutes);
    }

    private ECommandAction OnCommandMapInfo(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var tr       = _localization.For(slot);
        var profile  = _currentMapProfileInfo;
        var gameMode = _currentGameMode;

        // Line 1: Map name, tier, mode
        controller.PrintToChat(tr.Format(ChatTexts.MapInfo,
                                         Utils.Highlight(_bridge.CurrentMapName),
                                         Utils.Highlight(profile.Tier[0]),
                                         Utils.Highlight(gameMode)));

        // Line 2: Track layout — stages, checkpoints, bonuses, linear
        var totalStages      = _zoneModule.GetTotalStages(0);
        var totalCheckpoints = _zoneModule.GetCurrentTrackCheckpointCount(0);
        var isLinear         = _zoneModule.IsCurrentTrackLinear(0);

        var layout = tr.Format(ChatTexts.MapType, Utils.Highlight(tr[isLinear ? ChatTexts.MapLinear : ChatTexts.MapStaged]));

        if (totalStages > 0)
        {
            layout += tr.Format(ChatTexts.MapStages, Utils.Highlight(totalStages));
        }

        if (totalCheckpoints > 0)
        {
            layout += tr.Format(ChatTexts.MapCheckpoints, Utils.Highlight(totalCheckpoints));
        }

        if (profile.Bonuses > 0)
        {
            layout += tr.Format(ChatTexts.MapBonuses, Utils.Highlight(profile.Bonuses));
        }

        controller.PrintToChat(layout);

        // Line 3: WR and completions
        var wr    = _recordModule.GetWR(0, 0);
        var total = _recordModule.GetTotalRecordCount(0, 0);

        controller.PrintToChat(tr.Format(ChatTexts.MapSr,
                                         wr is not null
                                             ? Utils.ColoredTime(wr.Time)
                                             : string.Concat(ChatColor.Red, tr[ChatTexts.MapNoSr], ChatColor.White),
                                         Utils.Highlight(total)));

        // Line 4: Play count and total play time
        controller.PrintToChat(tr.Format(ChatTexts.MapPlayed,
                                         Utils.Highlight(profile.PlayCount),
                                         Utils.Highlight(Playtime(tr, profile.TotalPlayTime))));

        // Line 5: when it was added and last played (before this visit), as UTC dates
        controller.PrintToChat(tr.Format(ChatTexts.MapDates,
                                         Utils.Highlight(Date(tr, profile.AddedAt)),
                                         Utils.Highlight(Date(tr, profile.LastPlayedAt))));

        return ECommandAction.Handled;
    }

    // Read each map, so an edit applies from the next one.
    private void LoadGameModeConfig()
    {
        var config = GameModesConfig.Load(_gameModesPath, _logger).For(_bridge.CurrentMapName);

        _currentGameModeConfig = config;
        _currentGameMode       = config.GameMode;

        // Without a cfg of its own, the base cvars and the mode's go straight to the server.
        if (config.Cfg.Length == 0)
        {
            foreach (var cvar in _baseCvars.Concat(config.Cvars))
            {
                _bridge.ModSharp.ServerCommand(cvar);
            }

            return;
        }

        var configPath = Path.Combine(_configPath, config.Cfg);

        EnsureConfigExists(configPath, config);
        ExecuteGameModeConfig(configPath);
    }

    private void EnsureConfigExists(string path, GameModeConfig config)
    {
        if (File.Exists(path))
        {
            return;
        }

        try
        {
            File.WriteAllLines(path, _baseCvars);
            File.AppendAllLines(path, config.Cvars);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to create config at {p}", path);
        }
    }

    private void ExecuteGameModeConfig(string path)
    {
        try
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();

                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                {
                    continue;
                }

                var commentIndex = trimmed.IndexOf("//", StringComparison.Ordinal);

                var commandToExecute = commentIndex > 0
                    ? trimmed[..commentIndex].Trim()
                    : trimmed;

                if (!string.IsNullOrWhiteSpace(commandToExecute))
                {
                    _bridge.ModSharp.ServerCommand(commandToExecute);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to execute config {p}", path);
        }
    }

    private void LoadMapConfig()
    {
        var configPath = Path.Combine(_configPath, $"{_bridge.CurrentMapName}.json");

        if (!File.Exists(configPath))
        {
            return;
        }

        try
        {
            var content = File.ReadAllText(configPath);

            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            var mapConfig = JsonSerializer.Deserialize<MapConfig>(content);

            if (mapConfig == null)
            {
                return;
            }

            _currentMapConfig = mapConfig;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when reading map config {p}", configPath);

            return;
        }

        foreach (var command in _currentMapConfig.Commands)
        {
            _bridge.ModSharp.ServerCommand(command);
        }
    }

    public float GetEnterSpeedLimit(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.EnterSpeedLimit is { } enterLimit)
        {
            return enterLimit;
        }
        return _currentGameModeConfig.EnterSpeedLimit;
    }

    public int? GetZoneMaxJumpsOverride(int track)
        => _currentMapConfig != null && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone) ? zone.MaxJumps : null;

    public int GetGameModeMaxPrejumps()
        => _currentGameModeConfig.MaxPrejumps;

    public bool RequiresCheckpoints(int track)
        => _currentMapConfig?.ZoneConfigs.GetValueOrDefault(track)?.RequireCheckpoints ?? true;

    public EGameMode GetCurrentGameMode()
        => _currentGameMode;

    public float? GetZoneExitSpeedOverride(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone))
        {
            return zone.ExitSpeedLimit;
        }
        return null;
    }

    public float GetGameModeExitSpeedLimit()
        => _currentGameModeConfig.ExitSpeedLimit;

    public float GetStageEnterSpeedLimit(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.StageZone is { EnterSpeedLimit: { } stageEnterLimit })
        {
            return stageEnterLimit;
        }
        return GetEnterSpeedLimit(track);
    }

    public float? GetStageExitSpeedOverride(int track)
    {
        if (_currentMapConfig != null
            && _currentMapConfig.ZoneConfigs.TryGetValue(track, out var zone)
            && zone.StageZone is { } stageZone)
        {
            return stageZone.ExitSpeedLimit;
        }
        return GetZoneExitSpeedOverride(track);
    }

    public float GetGameModeWishSpeed()
        => _currentGameModeConfig.WishSpeed;

    public float GetDefaultAirAccelerate()
        => _currentGameModeConfig.AirAccelerate;

    public MapProfile GetCurrentMapProfile()
        => _currentMapProfileInfo;

    private void PersistCurrentMapStats()
    {
        if (_mapStatsPersisted)
        {
            return;
        }

        if (_currentMapStartTime <= 0 || string.IsNullOrEmpty(_bridge.CurrentMapName))
        {
            return;
        }

        _mapStatsPersisted = true;

        var delta = (float)(_bridge.ModSharp.EngineTime() - _currentMapStartTime);

        if (delta < 0f)
        {
            delta = 0f;
        }

        var map = _bridge.CurrentMapName;

        _taskTracker.Track(Task.Run(async () =>
        {
            try
            {
                await _requestManager.IncrementMapStatsAsync(map, delta).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when updating map info on shutdown");
            }
        }));
    }
}
