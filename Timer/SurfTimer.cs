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
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using SurfTimer.Managers;
using SurfTimer.Modules;

[assembly: DisableRuntimeMarshalling]

namespace SurfTimer;

public class SurfTimer : IModSharpModule
{
    private readonly InterfaceBridge         _bridge;
    private readonly ILogger<SurfTimer>      _logger;
    private readonly ServiceProvider         _serviceProvider;
    private readonly CancellationTokenSource _token;

    public SurfTimer(ISharedSystem   sharedSystem,
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

        var bridge = new InterfaceBridge(this,
                                         dllPath,
                                         sharpPath,
                                         version,
                                         sharedSystem,
                                         coreConfiguration,
                                         hotReload,
                                         token.Token,
                                         sharedSystem.GetModSharp()
                                                     .HasCommandLine("-debug"));

        var factory = sharedSystem.GetLoggerFactory();
        var logger  = factory.CreateLogger<SurfTimer>();

        var gameData = sharedSystem.GetModSharp()
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
        services.AddSingleton(sharedSystem);
        services.AddSingleton(gameData);
        /*services.AddSingleton<IConfiguration>(configuration);*/
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

    public void Shutdown()
    {
        try
        {
            _serviceProvider.GetRequiredService<IGameData>()
                            .Unregister("timer.games");

            foreach (var service in _serviceProvider.GetServices<IManager>())
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
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddLogging();

        services.AddManagerService();
        services.AddModuleService();
    }

    public T GetService<T>()
        => _serviceProvider.GetService<T>() ?? throw new ("Failed to get service");
}
