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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.CommandCenter.Shared;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Timer.MapChooser.Logic;

namespace Timer.MapChooser;

/// <summary>
///     Rock the vote, nominations, and the vote for the next map near the end of each one. It offers the server's
///     workshop maps with their tiers from the timer, and shows its votes through <see cref="IMapChooser" />.
/// </summary>
public sealed partial class MapChooserModule : IModSharpModule, IMapChooser, IGameListener, IClientListener
{
    private const string CommandCenterLibrary = "Sharp.Modules.CommandCenter";
    private const string AdminManagerLibrary  = "Sharp.Modules.AdminManager";
    private const float  TickInterval         = 0.25f;

    private static readonly string ModuleIdentity = typeof(MapChooserModule).Assembly.GetName().Name!;

    private readonly ISharedSystem             _shared;
    private readonly IModSharp                 _modSharp;
    private readonly IClientManager            _clients;
    private readonly ILogger<MapChooserModule> _logger;
    private readonly string                    _configPath;
    private readonly string                    _recentPath;
    private readonly string                    _prefix;
    private readonly Random                    _random = new ();

    private MapChooserConfig _config = new ();
    private RecentMaps       _recent = new (5);
    private Guid             _tick;

    private IModSharpModuleInterface<IRequestManager>?       _requests;
    private IModSharpModuleInterface<ILocalizationProvider>? _localization;
    private IModSharpModuleInterface<ICommandCenter>?        _commandCenter;
    private IModSharpModuleInterface<IAdminManager>?         _adminManager;
    private bool                                             _commandsRegistered;
    private bool                                             _adminCommandsRegistered;

    // The current map's state, reset when one starts.
    private readonly MapClock    _clock       = new ();
    private readonly Nominations _nominations = new ();
    private readonly RockTheVote _rtv         = new ();
    private readonly int[]       _cursor      = new int[PlayerSlot.MaxPlayerCount];

    private List<PoolMap>               _pool       = [];
    private Dictionary<string, PoolMap> _poolByName = new (StringComparer.OrdinalIgnoreCase);
    private int                         _poolEpoch;
    private int                         _poolVersion;
    private int                         _voteFailedVersion = -1; // the pool an end-of-map vote found nothing in
    private int                         _emptyWarnedEpoch  = -1;
    private string                      _currentMap = string.Empty;
    private bool                        _mapRunning;
    private MapVote?                    _vote;
    private string?                     _nextMap;
    private float                       _changeAt = float.NaN;
    private int                         _version;

    public MapChooserModule(ISharedSystem  sharedSystem,
                            string         dllPath,
                            string         sharpPath,
                            Version        version,
                            IConfiguration configuration,
                            bool           hotReload)
    {
        _shared     = sharedSystem;
        _modSharp   = sharedSystem.GetModSharp();
        _clients    = sharedSystem.GetClientManager();
        _logger     = sharedSystem.GetLoggerFactory().CreateLogger<MapChooserModule>();
        _configPath = Path.Combine(sharpPath, "configs", "timer-mapchooser.jsonc");
        _recentPath = Path.Combine(sharpPath, "data", "surftimer", "mapchooser-recent.json");
        _prefix     = ChatColorTags.LoadPrefix(Path.Combine(sharpPath, "configs", "timer.jsonc"));
    }

    public string DisplayName   => "SurfTimer Map Chooser";
    public string DisplayAuthor => "github.com/Nukoooo";

    public bool Init()
    {
        try
        {
            _config = MapChooserConfig.Load(_configPath);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to read {path}, using the defaults", _configPath);
            _config = new MapChooserConfig();
        }

        _recent               = new RecentMaps(_config.RecentMaps, LoadRecent());
        _nominations.Max      = _config.VoteMaps;

        _modSharp.InstallGameListener(this);
        _clients.InstallClientListener(this);
        _clients.InstallCommandListener(_config.KeyUp, OnKeyUp);
        _clients.InstallCommandListener(_config.KeyDown, OnKeyDown);
        _shared.GetHookManager().PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _tick = _modSharp.PushTimer(Tick, TickInterval, GameTimerFlags.Repeatable);

        return true;
    }

    public void PostInit()
    {
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<IMapChooser>(this, IMapChooser.Identity, this);
        Connect();
    }

    public void OnLibraryConnected(string name)
        => Connect();

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals(CommandCenterLibrary, StringComparison.OrdinalIgnoreCase))
        {
            _commandsRegistered      = false;
            _adminCommandsRegistered = false;
        }
        else if (name.Equals(AdminManagerLibrary, StringComparison.OrdinalIgnoreCase))
        {
            _adminCommandsRegistered = false;
        }
    }

    public void OnAllModulesLoaded()
    {
        Connect();

        if (!_commandsRegistered)
        {
            _logger.LogWarning("CommandCenter is not loaded: the map chooser's commands are unavailable");
        }
    }

    public void Shutdown()
    {
        _modSharp.StopTimer(_tick);
        _shared.GetHookManager().PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
        _clients.RemoveCommandListener(_config.KeyUp, OnKeyUp);
        _clients.RemoveCommandListener(_config.KeyDown, OnKeyDown);
        _clients.RemoveClientListener(this);
        _modSharp.RemoveGameListener(this);
    }

    private void Connect()
    {
        var modules = _shared.GetSharpModuleManager();

        if (_requests?.Instance is null)
        {
            _requests = modules.GetOptionalSharpModuleInterface<IRequestManager>(IRequestManager.Identity);

            if (_requests?.Instance is not null && _mapRunning)
            {
                RefreshPool();
            }
        }

        if (_localization?.Instance is null)
        {
            _localization = modules.GetOptionalSharpModuleInterface<ILocalizationProvider>(ILocalizationProvider.Identity);
        }

        if (_commandCenter?.Instance is null)
        {
            _commandCenter = modules.GetOptionalSharpModuleInterface<ICommandCenter>(ICommandCenter.Identity);
        }

        if (_adminManager?.Instance is null)
        {
            _adminManager = modules.GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity);
        }

        RegisterCommands();
    }

    // ------------------------------------------------------------------ map lifecycle

    int IGameListener.ListenerVersion  => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;

    void IGameListener.OnGameActivate()
        => StartMap();

    void IGameListener.OnGameDeactivate()
        => _mapRunning = false;

    private void StartMap()
    {
        _currentMap = _modSharp.GetMapName() ?? string.Empty;
        _mapRunning = true;
        _vote       = null;
        _nextMap    = null;
        _changeAt   = float.NaN;
        _nominations.Clear();
        _rtv.Clear();
        Array.Clear(_cursor);
        Array.Clear(_menus);

        _clock.Start(Now, _config.TimeLimit);

        if (_currentMap.Length > 0)
        {
            _recent.Push(_currentMap);
            SaveRecent();
        }

        RefreshPool();
        Changed();
    }

    /// <summary>
    ///     Builds the pool from the server's maps at once, then again with their tiers once the timer has them.
    /// </summary>
    private void RefreshPool()
    {
        var epoch  = ++_poolEpoch;
        var hosted = _modSharp.ListWorkshopMaps();
        var extra  = _config.ExtraMaps.Where(_modSharp.IsMapValid).ToList();

        SetPool(MapPool.Build(hosted, extra, [], _config.Exclude));

        if (_requests?.Instance is not { } requests)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var profiles = await requests.GetMapProfilesAsync();
                var pool     = MapPool.Build(hosted, extra, profiles, _config.Exclude);

                _modSharp.InvokeFrameAction(() =>
                {
                    if (epoch == _poolEpoch)
                    {
                        SetPool(pool);
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to read the map tiers");
            }
        });
    }

    private void SetPool(List<PoolMap> pool)
    {
        _pool       = pool;
        _poolByName = pool.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        _poolVersion++;
        Changed();

        if (pool.Count == 0 && _emptyWarnedEpoch != _poolEpoch)
        {
            _emptyWarnedEpoch = _poolEpoch;
            _logger.LogWarning("No maps to choose from: the server hosts no workshop maps (+host_workshop_collection) "
                               + "and extra_maps in {path} has none installed", _configPath);
        }
    }

    private IEnumerable<string> LoadRecent()
    {
        try
        {
            return File.Exists(_recentPath)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_recentPath)) ?? []
                : [];
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to read {path}", _recentPath);

            return [];
        }
    }

    private void SaveRecent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recentPath)!);
            File.WriteAllText(_recentPath, JsonSerializer.Serialize(_recent.Maps));
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to write {path}", _recentPath);
        }
    }

    // ------------------------------------------------------------------ players

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    void IClientListener.OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = (int) client.Slot;

        _rtv.Remove(slot);
        _nominations.Remove(slot);
        _vote?.Remove(slot);
        _cursor[slot] = 0;
        _menus[slot]  = null;
        Changed();

        if (_mapRunning)
        {
            CheckRockTheVote(client.Slot);
        }
    }

    private float Now => _modSharp.GetGlobals().CurTime;

    /// <summary>
    ///     Real players in the game, besides <paramref name="leaving" />.
    /// </summary>
    private int Players(PlayerSlot? leaving = null)
        => Humans().Count(x => x.Slot != leaving);

    private void Changed()
        => _version++;
}
