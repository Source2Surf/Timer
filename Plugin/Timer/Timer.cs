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
using System.IO;
using System.Linq;
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
        // Every record, zone and map read or write goes to Timer.Backend, set in timer.jsonc's backend section.
        var configuration = TimerConfiguration.Load(Path.Combine(sharpPath, "configs", "timer.jsonc"));
        var backend       = BackendOptions.FromConfiguration(configuration);
        services.AddSingleton(configuration);
        services.AddSingleton(backend);
        logger.LogInformation("Timer.Backend: {endpoint}", backend.Endpoint);

        if (configuration.GetSection("database").Exists())
        {
            logger.LogWarning("timer.jsonc's database section is no longer read: Timer.Backend owns the database. Remove it.");
        }
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
        RefreshCommandManager();
        RefreshReplayProvider();
        RefreshPermissionProvider();
        RefreshLocalizationProvider();
        RefreshMapChooser();
        RefreshMovementExtension();

        _serviceProvider.GetRequiredService<ISharedSystem>()
                        .GetSharpModuleManager()
                        .RegisterSharpModuleInterface<ITimerStyles>(this, ITimerStyles.Identity, _serviceProvider.GetRequiredService<ITimerStyles>());

        _serviceProvider.GetRequiredService<ISharedSystem>()
                        .GetSharpModuleManager()
                        .RegisterSharpModuleInterface<IRequestManager>(this, IRequestManager.Identity, _serviceProvider.GetRequiredService<IRequestManager>());
    }

    public void OnLibraryConnected(string moduleIdentity)
    {
        RefreshMovementExtension();
        _serviceProvider.GetService<CommandManager>()?.ConnectAdminManager();

        if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
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
        _serviceProvider.GetService<CommandManager>()?.OnLibraryDisconnect(moduleIdentity);

        if (moduleIdentity.Equals(IReplayProvider.Identity, StringComparison.Ordinal))
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
        RefreshCommandManager();
        if (_serviceProvider.GetService<CommandManager>() is { } commands && !commands.ConnectAdminManager())
        {
            _logger.LogWarning("ModSharp's AdminManager is not loaded: admin commands such as !zone are unavailable.");
        }
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

            // Detours call into the modules, so remove them before the modules go.
            var hookManager = _serviceProvider.GetService<IInlineHookManager>() as IManager;
            if (hookManager is not null)
            {
                try
                {
                    hookManager.Shutdown();
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "An error occurred while shutting down the inline hook manager.");
                }
            }

            // The modules use the managers, so they go next.
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

            // Reverse order: a manager may use the ones registered before it.
            foreach (var service in _serviceProvider.GetServices<IManager>().Reverse())
            {
                if (ReferenceEquals(service, hookManager))
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
