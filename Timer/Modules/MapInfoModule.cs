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
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using SurfTimer.Managers;
using SurfTimer.Managers.Player;
using SurfTimer.Managers.Request.Models;
using SurfTimer.Modules.MapInfo;

namespace SurfTimer.Modules;

internal interface IMapInfoModule
{
    float GetMaxPreSpeed(int track);

    float GetDefaultAirAccelerate();

    MapProfile GetCurrentMapProfile();
}

internal class MapInfoModule : IModule, IMapInfoModule, IGameListener
{
    private readonly InterfaceBridge _bridge;
    private readonly IRequestManager _requestManager;
    private readonly ICommandManager _commandManager;

    private readonly ILogger<MapInfoModule>          _logger;

    private MapProfile _currentMapProfileInfo = null!;

    private string _currentMapName = string.Empty;

    private double     _currentMapStartTime;
    private MapConfig? _currentMapConfig = null;

    private readonly string _configPath;

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
    ];

    private static readonly IReadOnlyList<GameModeConfig> GameModeConfigs =
    [
        new ("surf", "surf.cfg", ["sv_airaccelerate 150"], EGameMode.Surf),
        new ("bhop", "bhop.cfg", ["sv_airaccelerate 1000"], EGameMode.Bhop),
    ];

    private EGameMode _currentGameMode = EGameMode.None;

    private float _currentAirAccelerate;

    public MapInfoModule(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ICommandManager        commandManager,
                         ILogger<MapInfoModule> logger)
    {
        _bridge         = bridge;
        _requestManager = requestManager;
        _commandManager = commandManager;
        _logger         = logger;

        _configPath = Path.Combine(bridge.TimerDataPath, "map_configs");

        if (!Directory.Exists(_configPath))
        {
            Directory.CreateDirectory(_configPath);
        }
    }

    public bool Init()
    {
        _bridge.ModSharp.InstallGameListener(this);

        _commandManager.AddAdminChatCommand("tier", OnCommandTier);

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    public void OnGameInit()
    {
        _currentMapName      = _bridge.GlobalVars.MapName;
        _currentMapStartTime = _bridge.ModSharp.EngineTime();

        Task.Run(async () =>
        {
            _currentMapProfileInfo = await _requestManager.GetMapInfo(_currentMapName).ConfigureAwait(false);
        });
    }

    public void OnGameActivate()
    {
        LoadGameModeConfig();
        LoadMapConfig();

        _currentAirAccelerate = _currentGameMode switch
        {
            EGameMode.None => 150.0f,
            EGameMode.Surf => 150.0f,
            EGameMode.Bhop => 1000.0f,
            _              => 1000.0f,
        };
    }

    public void OnGamePreShutdown()
    {
        _currentMapConfig = null;
        var delta = (float) (_currentMapStartTime - _currentMapStartTime);

        Task.Run(() =>
        {
            _currentMapProfileInfo.PlayCount++;
            _currentMapProfileInfo.TotalPlayTime += delta;

            _requestManager.UpdateMapInfo(_currentMapProfileInfo);
        });
    }

    private ECommandAction OnCommandTier(IGamePlayer player, StringCommand command)
    {
        if (command.ArgCount < 1)
        {
            return ECommandAction.Handled;
        }

        if (command.TryGet<byte>(1) is var tier)
        {
            _currentMapProfileInfo.Tier[0] = tier;

            Task.Run(async () => await _requestManager.UpdateMapInfo(_currentMapProfileInfo).ConfigureAwait(false));
        }

        return ECommandAction.Handled;
    }

    private void LoadGameModeConfig()
    {
        var             configPath = "";
        GameModeConfig? config     = null;

        foreach (var cfg in GameModeConfigs)
        {
            if (!_currentMapName.StartsWith(cfg.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            config           = cfg;
            configPath       = Path.Combine(_configPath, cfg.FileName);
            _currentGameMode = cfg.GameMode;

            break;
        }

        if (config == null)
        {
            foreach (var cvar in _baseCvars)
            {
                _bridge.ModSharp.ServerCommand(cvar);
            }

            return;
        }

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
            File.AppendAllLines(path, config.SpecificCvars);
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
        var configPath = Path.Combine(_configPath, $"{_currentMapName}.json");

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

    private record GameModeConfig
    {
        public string    Prefix        { get; }
        public string    FileName      { get; }
        public string[]  SpecificCvars { get; }
        public EGameMode GameMode      { get; }

        public GameModeConfig(string prefix, string fileName, string[] specificCvars, EGameMode gameMode)
        {
            Prefix        = prefix;
            FileName      = fileName;
            SpecificCvars = specificCvars;
            GameMode      = gameMode;
        }
    }

    public float GetMaxPreSpeed(int track)
    {
        if (_currentGameMode is EGameMode.None or EGameMode.Bhop
            || _currentMapConfig == null
            || !_currentMapConfig.ZoneConfigs.TryGetValue(track, out var value))
        {
            return 290.0f;
        }

        if (value.PreSpeed is { } preSpeed)
        {
            return preSpeed;
        }

        return 290.0f;
    }

    public float GetDefaultAirAccelerate()
        => _currentAirAccelerate;

    public MapProfile GetCurrentMapProfile()
        => _currentMapProfileInfo;
}
