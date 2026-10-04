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
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.MapChooser;
using Source2Surf.Timer.Managers.Movement;
using Source2Surf.Timer.Managers.Permission;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Replay;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Shared.Interfaces;

[assembly: DisableRuntimeMarshalling]

namespace Source2Surf.Timer;

public class Timer : IModSharpModule
{
    private readonly InterfaceBridge         _bridge;
    private readonly ILogger<Timer>          _logger;
    private readonly ServiceProvider         _serviceProvider;
    private readonly CancellationTokenSource _token;
    private int                              _shutdownState;

    public Timer(ISharedSystem   shared,
                 string?         dllPath,
                 string?         sharpPath,
                 Version?        version,
                 IConfiguration? coreConfiguration,
                 bool            hotReload)
    {
        ArgumentNullException.ThrowIfNull(dllPath);
        ArgumentNullException.ThrowIfNull(sharpPath);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(coreConfiguration);

        var token = new CancellationTokenSource();

        /*var configuration = new ConfigurationBuilder()
                            .AddJsonFile(Path.Combine(dllPath, "appsettings.json"), false, false)
                            .Build();*/

        var bridge = new InterfaceBridge(sharpPath, shared, token.Token);

        var factory = shared.GetLoggerFactory();
        var logger  = factory.CreateLogger<Timer>();

        var gameData = shared.GetModSharp()
                             .GetGameData();

        gameData.Register("timer.games");

        /*if (File.Exists(Path.Combine(sharpPath, "gamedata", "test.games.kv")))
        {
            gameData.Register("test.games");
            _testGameData = true;
        }*/

        var services = new ServiceCollection();

        services.AddSingleton(bridge);
        services.AddSingleton(factory);
        services.AddSingleton(shared);
        services.AddSingleton(gameData);
        // The host owns this configuration. Remote score-write mode is explicitly selected
        // from it; registering the existing instance adds no file watcher or network I/O.
        services.AddSingleton<IConfiguration>(coreConfiguration);
        services.AddSingleton(ScoreWriteModeOptions.FromConfiguration(coreConfiguration));
        /*ConfigureDebugServices(services, bridge);*/
        ConfigureServices(services);

        _serviceProvider = services.BuildServiceProvider();

        _token  = token;
        _bridge = bridge;
        _logger = logger;
    }

    public string DisplayName   => "SurfTimer";
    public string DisplayAuthor => "github.com/Nukoooo";

    public bool Init()
    {
        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init service {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            if (service.Init())
            {
#if DEBUG
                _logger.LogInformation("Init module {service}!",
                                       service.GetType()
                                              .FullName);
#endif
                continue;
            }

            _logger.LogError("Failed to init {service}!",
                             service.GetType()
                                    .FullName);

            return false;
        }

        foreach (var service in _serviceProvider.GetServices<IManager>())
        {
            try
            {
                service.OnPostInit();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        foreach (var service in _serviceProvider.GetServices<IModule>())
        {
            try
            {
                service.OnPostInit(_serviceProvider);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "An error occurred while calling PostInit for {type}", service.GetType().FullName);
            }
        }

        return true;
    }

    public void PostInit()
    {
        RefreshRequestManager();
        RefreshCommandManager();
        RefreshReplayProvider();
        RefreshPermissionProvider();
        RefreshLocalizationProvider();
        RefreshMapChooser();
        RefreshMovementExtension();

        _serviceProvider.GetRequiredService<ISharedSystem>()
                        .GetSharpModuleManager()
                        .RegisterSharpModuleInterface<ITimerStyles>(this, ITimerStyles.Identity, _serviceProvider.GetRequiredService<ITimerStyles>());
    }

    public void OnLibraryConnected(string moduleIdentity)
    {
        RefreshMovementExtension();

        if (moduleIdentity.Equals(IRequestManager.Identity, StringComparison.Ordinal))
        {
            RefreshRequestManager();
        }
        else if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
        {
            RefreshReplayProvider();
        }
        else if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            RefreshCommandManager();
        }
        else if (moduleIdentity.Equals(IPermissionProvider.Identity, StringComparison.Ordinal))
        {
            RefreshPermissionProvider();
        }
        else if (moduleIdentity.Equals(ILocalizationProvider.Identity, StringComparison.Ordinal))
        {
            RefreshLocalizationProvider();
        }
        else if (IsMapChooser(moduleIdentity))
        {
            RefreshMapChooser();
        }
    }

    public void OnLibraryDisconnect(string moduleIdentity)
    {
        _serviceProvider.GetService<MovementExtensionProxy>()?.OnModuleUnloading(moduleIdentity);

        if (moduleIdentity.Equals(IRequestManager.Identity, StringComparison.Ordinal))
        {
            SwitchRequestManagerToUnavailable();
        }
        else if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
        {
            // RefreshProvider re-resolves; with the module gone it clears the provider
            // instead of holding a dead reference.
            RefreshReplayProvider();
        }
        else if (moduleIdentity.Equals(ICommandManager.Identity, StringComparison.Ordinal))
        {
            SwitchCommandManagerToFallback();
        }
        else if (moduleIdentity.Equals(IPermissionProvider.Identity, StringComparison.Ordinal))
        {
            _serviceProvider.GetService<PermissionProviderProxy>()?.UseFallback();
        }
        else if (moduleIdentity.Equals(ILocalizationProvider.Identity, StringComparison.Ordinal))
        {
            _serviceProvider.GetService<LocalizationProviderProxy>()?.UseFallback();
        }
        else if (IsMapChooser(moduleIdentity))
        {
            _serviceProvider.GetService<MapChooserProxy>()?.UseFallback();
        }
    }

    public void OnAllModulesLoaded()
    {
        RefreshRequestManager();
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy { IsAvailable: false })
        {
            _logger.LogError(
                "No external IRequestManager was registered after modules loaded. Timer reads and non-score writes will fail closed; install/configure Timer.RequestManager or a complete remote read provider.");
        }

        RefreshCommandManager();
        RefreshReplayProvider();
        RefreshPermissionProvider();
        RefreshLocalizationProvider();
        RefreshMapChooser();
        RefreshMovementExtension();
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0)
        {
            return;
        }

        try
        {
            _serviceProvider.GetRequiredService<IGameData>()
                            .Unregister("timer.games");

            // Login profile RPCs share the sender's channel. Cancel those per-session calls
            // before the sender drains scores and disposes that transport.
            var playerManager = _serviceProvider.GetService<IPlayerManager>() as IManager;
            if (playerManager is not null)
            {
                try
                {
                    playerManager.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while shutting down the player manager.");
                }
            }

            foreach (var service in _serviceProvider.GetServices<IManager>())
            {
                if (ReferenceEquals(service, playerManager))
                {
                    continue;
                }

                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            foreach (var service in _serviceProvider.GetServices<IModule>())
            {
                try
                {
                    service.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while calling Shutdown for {type}", service.GetType().FullName);
                }
            }

            _token.Cancel();

            _logger.LogInformation("Shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error when shutting down");

            // ignored
        }
        finally
        {
            try
            {
                _serviceProvider.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when disposing ServiceProvider");
            }

            _token.Dispose();
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddManagerService();
        services.AddModuleService();
    }

    private void RefreshRequestManager()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, skip refresh.");
    }

    private void SwitchRequestManagerToUnavailable()
    {
        if (_serviceProvider.GetService<IRequestManager>() is RequestManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("IRequestManager is not RequestManagerProxy, cannot mark the external provider unavailable.");
    }

    private void RefreshCommandManager()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.RefreshManager();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, skip refresh.");
    }

    private void SwitchCommandManagerToFallback()
    {
        if (_serviceProvider.GetService<ICommandManager>() is CommandManagerProxy proxy)
        {
            proxy.UseFallback();

            return;
        }

        _logger.LogWarning("ICommandManager is not CommandManagerProxy, cannot force fallback.");
    }

    private void RefreshReplayProvider()
    {
        _serviceProvider.GetService<ReplayProviderProxy>()?.RefreshProvider();
    }

    private void RefreshPermissionProvider()
    {
        _serviceProvider.GetService<PermissionProviderProxy>()?.RefreshManager();
    }

    private void RefreshLocalizationProvider()
    {
        _serviceProvider.GetService<LocalizationProviderProxy>()?.RefreshManager();
    }

    private void RefreshMapChooser()
    {
        _serviceProvider.GetService<MapChooserProxy>()?.RefreshManager();
    }

    private void RefreshMovementExtension()
    {
        _serviceProvider.GetService<MovementExtensionProxy>()?.RefreshManager();
    }

    // ModSharp passes the module's assembly name here.
    private static bool IsMapChooser(string module)
        => module.Equals(IMapChooser.Identity, StringComparison.Ordinal) || module.Equals("Timer.MapChooser", StringComparison.OrdinalIgnoreCase);
}
