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
using System.IO;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;

namespace SurfTimer;

internal interface IModule
{
    bool Init();

    void OnPostInit(ServiceProvider provider)
    {
    }

    void Shutdown()
    {
    }
}

internal interface IManager
{
    bool Init();

    void OnPostInit()
    {
    }

    void Shutdown();
}

internal class InterfaceBridge
{
    private readonly SurfTimer     _entrypoint;

    public InterfaceBridge(SurfTimer         entrypoint,
                           string            dllPath,
                           string            sharpPath,
                           Version           version,
                           ISharedSystem     sharedSystem,
                           IConfiguration    coreConfig,
                           bool              hotreload,
                           CancellationToken token,
                           bool              debug)
    {
        _entrypoint       = entrypoint;
        DllPath           = dllPath;
        SharpPath         = sharpPath;
        Version           = version;
        CoreConfig        = coreConfig;
        Hotreload         = hotreload;
        CancellationToken = token;
        Debug             = debug;

        TimerDataPath = Path.Combine(sharpPath, "data", "surftimer");

        if (!Directory.Exists(TimerDataPath))
        {
            Directory.CreateDirectory(TimerDataPath);
        }

        ModSharp            = sharedSystem.GetModSharp();
        ConVarManager       = sharedSystem.GetConVarManager();
        EventManager        = sharedSystem.GetEventManager();
        ClientManager       = sharedSystem.GetClientManager();
        EntityManager       = sharedSystem.GetEntityManager();
        FileManager         = sharedSystem.GetFileManager();
        HookManager         = sharedSystem.GetHookManager();
        SchemaManager       = sharedSystem.GetSchemaManager();
        TransmitManager     = sharedSystem.GetTransmitManager();
        PhysicsQueryManager = sharedSystem.GetPhysicsQueryManager();
        LoggerFactory       = sharedSystem.GetLoggerFactory();
        Modules             = sharedSystem.GetLibraryModuleManager();
    }

    public string DllPath { get; }

    public string SharpPath { get; }

    public string TimerDataPath { get; }

    public Version Version { get; }

    public IConfiguration CoreConfig { get; }

    public bool Hotreload { get; }

    public CancellationToken CancellationToken { get; }

    public bool Debug { get; }

    public IModSharp             ModSharp { get; }
    public ILibraryModuleManager Modules  { get; }

    public IConVarManager       ConVarManager       { get; }
    public IEventManager        EventManager        { get; }
    public IClientManager       ClientManager       { get; }
    public IEntityManager       EntityManager       { get; }
    public IFileManager         FileManager         { get; }
    public IHookManager         HookManager         { get; }
    public ISchemaManager       SchemaManager       { get; }
    public ITransmitManager     TransmitManager     { get; }
    public IPhysicsQueryManager PhysicsQueryManager { get; }
    public ILoggerFactory       LoggerFactory       { get; }

    public IGameRules     GameRules  => ModSharp.GetGameRules();
    public IGlobalVars    GlobalVars => ModSharp.GetGlobals();
    public INetworkServer Server     => ModSharp.GetIServer();

    public T GetService<T>()
        => _entrypoint.GetService<T>();
}
