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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Iced.Intel;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using SurfTimer.Extensions;
using SurfTimer.Managers;
using SurfTimer.Modules.Zone;
using ZLinq;

namespace SurfTimer.Modules;

internal interface IZoneModule
{
    delegate void OnZoneFireOutputDelegate(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn);

    public event OnZoneFireOutputDelegate OnStartTouch;
    public event OnZoneFireOutputDelegate OnEndTouch;
    public event OnZoneFireOutputDelegate OnTrigger;

    void AddZone(ZoneInfo info);

    bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type);

    bool IsCurrentTrackLinear(int track);

    bool HasZone(int track, EZoneType type);

    bool CurrentTrackHasCheckpoints(int track);

    int GetTotalStages(int track);

    int GetCurrentTrackCheckpointCount(int track);
}

// TODO:

internal partial class ZoneModule : IModule, IZoneModule, IEntityListener, IGameListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IPlayerManager       _playerManager;

    private readonly ILogger<ZoneModule> _logger;

    private static readonly int[] CurrentMaxStages = new int[Utils.MAX_TRACK];

    private readonly        Dictionary<uint, ZoneInfo> _zones            = [];
    private static readonly BuildZoneInfo?[]           BuildZoneInfo;

    private readonly string _zonePath;

    // ReSharper disable InconsistentNaming
    private unsafe delegate* unmanaged<Vector*, Vector*, Vector*, nint> CreateTrigger;

    // ReSharper restore InconsistentNaming

    static ZoneModule()
        => BuildZoneInfo = Enumerable.Repeat<BuildZoneInfo?>(null, PlayerSlot.MaxPlayerSlot).ToArray();

    public ZoneModule(InterfaceBridge     bridge,
                      IPlayerManager      playerManager,
                      ICommandManager     commandManager,
                      ILogger<ZoneModule> logger)
    {
        _bridge         = bridge;
        _logger         = logger;
        _playerManager  = playerManager;
        _commandManager = commandManager;

        _zonePath = Path.Combine(bridge.TimerDataPath, "zones");

        if (!Directory.Exists(_zonePath))
        {
            Directory.CreateDirectory(_zonePath);
        }
    }

    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (entity.Classname != "trigger_multiple")
        {
            return;
        }

        var targetName = entity.Name;

        _logger.LogInformation("Entity {classname} spawned with name {targetname}", entity.Classname, targetName);

        if (string.IsNullOrEmpty(targetName) || string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        // 如果是我们自己加的就不管
        if (targetName.StartsWith("surftimer_zone_"))
        {
            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Start) || AddPrebuiltZone(entity, targetName, EZoneType.End))
        {
            CreateBeam(entity.Handle);
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);

            entity.SetNetVar("m_flWait", 0.001f);

            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Stage) || AddPrebuiltZone(entity, targetName, EZoneType.Checkpoint))
        {
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);

            entity.SetNetVar("m_flWait", 0.001f);

            return;
        }

        _logger.LogWarning("{t} is not the zone we want", targetName);
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        if (!_zones.Remove(entity.Handle.GetValue(), out var info) || info.Beams is not { } beams)
        {
            return;
        }

        foreach (var beam in beams)
        {
            if (beam is { IsValidEntity: true })
            {
                beam.Kill();
            }
        }
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        if (activator?.AsPlayerPawn() is not { IsValidEntity: true } pawn
            || pawn.GetController() is not { IsValidEntity : true } controller
            || controller.IsFakeClient)
        {
            return EHookAction.Ignored;
        }

        var entityHandle = entity.Handle.GetValue();

        if (!_zones.TryGetValue(entityHandle, out var info))
        {
            return EHookAction.Ignored;
        }

        switch (output)
        {
            case "onstarttouch":
            {
                OnStartTouch?.Invoke(info, controller, pawn);

                break;
            }
            case "onendtouch":
            {
                OnEndTouch?.Invoke(info, controller, pawn);

                break;
            }
            case "ontrigger":
            {
                OnTrigger?.Invoke(info, controller, pawn);

                break;
            }
        }

        return EHookAction.Ignored;
    }

    public void OnGamePostInit()
    {
        if (!FindTrigger())
        {
            throw new InvalidOperationException("Failed to find CreateTriggerInternal");
        }

        for (var i = 0; i < Utils.MAX_TRACK; i++)
        {
            CurrentMaxStages[i] = -1;
        }
    }

    public void OnGameShutdown()
    {
        for (var i = 0; i < Utils.MAX_TRACK; i++)
        {
            CurrentMaxStages[i] = -1;
        }

        _zones.Clear();
    }

    public void OnServerActivate()
    {
        LoadCustomZones();

        _bridge.ModSharp.InvokeFrameAction(FindZoneStartPosition);
    }

    public bool Init()
    {
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnStartTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnEndTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTouching");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTrigger");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTouchingEachEntity");

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.EntityManager.InstallEntityListener(this);

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);

        _commandManager.AddAdminChatCommand("zone", OnCommandZone);

        return true;
    }

    public void Shutdown()
    {
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        OnStartTouch = null;
        OnEndTouch   = null;
        OnTrigger    = null;
    }

    public event IZoneModule.OnZoneFireOutputDelegate? OnStartTouch;
    public event IZoneModule.OnZoneFireOutputDelegate? OnEndTouch;
    public event IZoneModule.OnZoneFireOutputDelegate? OnTrigger;

    public unsafe void AddZone(ZoneInfo info)
    {
        var targetName = $"surftimer_{info.Track}_{info.ZoneType}_{info.Data}".ToLowerInvariant();

        var infoCorner1 = info.Corner1;
        var infoCorner2 = info.Corner2;

        var mins = new Vector();
        var maxs = new Vector();

        for (var i = 0; i < 3; i++)
        {
            maxs[i] = Math.Abs(infoCorner1[i] - infoCorner2[i]) / 2.0f;
            mins[i] = -maxs[i];
        }

        var origin = (infoCorner1 + infoCorner2) / 2.0f;
        origin.Z = infoCorner1.Z + 2f;

        var entPtr = CreateTrigger(&origin, &mins, &maxs);

        if (entPtr == 0)
        {
            throw new ("Failed to create rigger");
        }

        if (_bridge.EntityManager.MakeEntityFromPointer<IBaseTrigger>(entPtr) is not { } ent)
        {
            return;
        }

        ent.SetName(targetName);

        ent.SpawnFlags =  4097;
        ent.Effects    |= EntityEffects.NoDraw;
        ent.SetNetVar("m_flWait", 0.015625f);

        info.Origin = origin;
        info.Index  = ent.Index;

        _zones.TryAdd(ent.Handle.GetValue(), info);

        if (info.ZoneType is EZoneType.Start or EZoneType.End)
        {
            CreateBeam(ent.Handle);
        }
    }

    public bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type)
    {
        foreach (var (_, zoneInfo) in _zones)
        {
            if (zoneInfo.Track != track || type != zoneInfo.ZoneType)
            {
                continue;
            }

            pawn.Teleport(zoneInfo.TeleportOrigin ?? zoneInfo.Origin, null, new Vector());

            return true;
        }

        return false;
    }

    public bool IsCurrentTrackLinear(int track)
        => CurrentMaxStages[track] <= 1;

    public bool HasZone(int track, EZoneType type)
        => _zones.AsValueEnumerable().Any(i => i.Value.Track == track && i.Value.ZoneType == type);

    public bool CurrentTrackHasCheckpoints(int track)
        => _zones.AsValueEnumerable().Any(i => i.Value.Track == track && i.Value.ZoneType == EZoneType.Checkpoint);

    public int GetTotalStages(int track)
        => CurrentMaxStages[track];

    public int GetCurrentTrackCheckpointCount(int track)
        => _zones.AsValueEnumerable().Count(i => i.Value.Track == track && i.Value.ZoneType == EZoneType.Checkpoint);

    private bool AddPrebuiltZone(IBaseEntity entity, string targetName, EZoneType type)
    {
        if (entity.GetCollisionProperty() is not { } collision)
        {
            return false;
        }

        var handle = entity.Handle.GetValue();
        var origin = entity.GetAbsOrigin();
        origin.Z += 2;
        var corner1 = collision.Mins + origin;
        var corner2 = collision.Maxs + origin;

        // NormalizeZonePoints(ref corner1, ref corner2);

        var info = new ZoneInfo
        {
            ZoneType   = type,
            Corner1    = corner1,
            Corner2    = corner2,
            Origin     = origin,
            Index      = entity.Index,
            Prebuilt   = true,
            TargetName = targetName,
        };

        int track;

        switch (type)
        {
            case EZoneType.Start:
            {
                if (!ZoneMatcher.IsBonusStartZone(targetName, out track))
                {
                    return ZoneMatcher.IsStartZone(targetName) && _zones.TryAdd(handle, info);
                }

                if (CurrentMaxStages[track] < 1)
                {
                    CurrentMaxStages[track] = 1;
                }

                info.Track = track;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.End:
            {
                if (!ZoneMatcher.IsBonusEndZone(targetName, out track))
                {
                    return ZoneMatcher.IsEndZone(targetName) && _zones.TryAdd(handle, info);
                }

                info.Track = track;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.Stage:
            {
                if (!ZoneMatcher.IsStageZone(targetName, out var stage))
                {
                    return false;
                }

                if (stage >= CurrentMaxStages[0])
                {
                    CurrentMaxStages[0] = stage;
                }

                info.Data = stage;

                return _zones.TryAdd(handle, info);
            }
            case EZoneType.Checkpoint:
            {
                if (ZoneMatcher.IsCheckpointZone(targetName, out var cp))
                {
                    info.Data = cp;

                    return _zones.TryAdd(handle, info);
                }

                if (ZoneMatcher.IsBonusCheckpointZone(targetName, out var bonusTrack, out cp))
                {
                    info.Track = bonusTrack;
                    info.Data  = cp;

                    return _zones.TryAdd(handle, info);
                }

                break;
            }
        }

        return false;
    }

    private void CreateBeam(uint handle)
    {
        if (!_zones.TryGetValue(handle, out var val))
        {
            _logger.LogInformation("Failed to get value for entity handle 0x{hand:X}", handle);

            return;
        }

        var p1 = val.Corner1;
        var p2 = val.Corner2;

        Span<Vector> points =
        [
            p1,                     // back,  left,  bottom
            new (p1.X, p2.Y, p1.Z), // back,  right, bottom
            new (p2.X, p2.Y, p1.Z), // front, right, bottom
            new (p2.X, p1.Y, p1.Z), // front, left,  bottom
        ];

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", val.ZoneType == EZoneType.Start ? "0 255 0" : "255 0 0" },
            { "BoltWidth", "6" },
        };

        val.Beams = new IBaseEntity[points.Length];

        for (var i = 0; i < points.Length; i++)
        {
            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not { IsValidEntity: true } beam)
            {
                continue;
            }

            beam.SetAbsOrigin(points[i]);
            beam.SetNetVar("m_vecEndPos", points[i == 3 ? 0 : i + 1]);
            val.Beams[i] = beam;
        }
    }

    private HookReturnValue<EmptyHookReturn> OnPlayerRunCommandPre(IPlayerRunCommandHookParams      arg1,
                                                                   HookReturnValue<EmptyHookReturn> arg2)
    {
        var client = arg1.Client;

        if (client.IsFakeClient)
        {
            return new ();
        }

        var pawn = arg1.Pawn;

        if (!pawn.IsAlive || BuildZoneInfo[client.Slot] is not { } buildInfo)
        {
            return new ();
        }

        var eyepos = pawn.GetEyePosition();
        eyepos.Z -= 2f;

        var direction = pawn.GetEyeAngles().AnglesToVectorForward();

        var end = eyepos + (direction * 1024.0f);

        var attribute = RnQueryShapeAttr.Bullets();
        attribute.HitTrigger = false;
        attribute.SetEntityToIgnore(pawn, 0);

        var result = _bridge.PhysicsQueryManager.TraceLineNoPlayers(eyepos, end, attribute);

        const int snapGrid = 2;

        var snapped = SnapToGrid(result.EndPosition + new Vector(0, 0, 3), snapGrid);

        RenderDirectionBeam(buildInfo, eyepos, snapped);
        RenderSnapBeams(buildInfo, snapped, snapGrid);

        buildInfo.RenderPreviewBeams(buildInfo.Points[0],
                                     buildInfo.Step == 1 ? snapped + new Vector(0, 0, 128) : buildInfo.Points[1]);

        if ((arg1.KeyButtons & UserCommandButtons.Use) == 0 || (arg1.ChangedButtons & UserCommandButtons.Use) == 0)
        {
            return new ();
        }

        if (buildInfo.Step == 0)
        {
            buildInfo.Points[0] = snapped;

            if (buildInfo.RenderBeams[0] == null)
            {
                var kv = new Dictionary<string, KeyValuesVariantValueItem>
                {
                    { "rendercolor", "255 255 255" },
                    { "BoltWidth", "6" },
                };

                for (var i = 0; i < buildInfo.RenderBeams.Length; i++)
                {
                    if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not
                    {
                        IsValidEntity: true,
                    } beam)
                    {
                        return new ();
                    }

                    buildInfo.RenderBeams[i] = beam;
                }
            }

            buildInfo.Step++;
        }
        else if (buildInfo.Step == 1)
        {
            buildInfo.Points[1] = snapped + new Vector(0, 0, 128);

            AddZone(new ()
            {
                Track    = buildInfo.Track,
                ZoneType = buildInfo.Zone,
                Prebuilt = false,
                Corner1  = buildInfo.Points[0],
                Corner2  = buildInfo.Points[1],
            });

            buildInfo.KillBeams();

            BuildZoneInfo[client.Slot] = null;

            Task.Run(SaveCustomZones);
        }

        return new ();
    }

    private static void RenderSnapBeams(BuildZoneInfo buildInfo, Vector snapped, int snapGrid)
    {
        var snapBeams = buildInfo.SnapBeams;

        // forward <-> backwards
        snapBeams[0].SetAbsOrigin(snapped             + ((new Vector(1,  0, 0) * snapGrid) / 2));
        snapBeams[0].SetNetVar("m_vecEndPos", snapped + ((new Vector(-1, 0, 0) * snapGrid) / 2));

        // left <-> right
        snapBeams[1].SetAbsOrigin(snapped             + ((new Vector(0, -1, 0) * snapGrid) / 2));
        snapBeams[1].SetNetVar("m_vecEndPos", snapped + ((new Vector(0, 1,  0) * snapGrid) / 2));
    }

    private void RenderDirectionBeam(BuildZoneInfo buildInfo, Vector eyepos, Vector snapped)
    {
        if (buildInfo.DirectionBeam is not { } directionBeam)
        {
            var kv = new Dictionary<string, KeyValuesVariantValueItem>
            {
                { "rendercolor", "255 255 255" },
                { "BoltWidth", "6" },
            };

            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is not { IsValidEntity: true } beam)
            {
                return;
            }

            beam.SetAbsOrigin(eyepos);
            beam.SetNetVar("m_vecEndPos", snapped);
            buildInfo.DirectionBeam = beam;
        }
        else
        {
            directionBeam.SetAbsOrigin(eyepos);
            directionBeam.SetNetVar("m_vecEndPos", snapped);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector SnapToGrid(in Vector pos, int grid)
    {
        if (grid <= 1)
        {
            return pos;
        }

        var gridF = (float) grid;

        var snappedX = (float) Math.Round(pos.X / gridF, MidpointRounding.AwayFromZero) * gridF;
        var snappedY = (float) Math.Round(pos.Y / gridF, MidpointRounding.AwayFromZero) * gridF;

        return new (snappedX, snappedY, pos.Z);
    }

    private void FindZoneStartPosition()
    {
        IBaseEntity? ent = null;

        while ((ent = _bridge.EntityManager.FindEntityByClassname(ent, "info_teleport_destination")) != null)
        {
            var origin = ent.GetAbsOrigin();

            foreach (var (handle, info) in _zones)
            {
                if (_bridge.EntityManager.FindEntityByHandle(new CEntityHandle<IBaseTrigger>(handle)) is not { } trigger
                    || trigger.GetCollisionProperty() is not { } collision)
                {
                    continue;
                }

                var triggerOrigin = trigger.GetAbsOrigin();

                if (!IsPointInBox(origin, triggerOrigin, collision.BoundingRadius))
                {
                    continue;
                }

                info.TeleportAngles = ent.GetAbsAngles();
                info.TeleportOrigin = origin;

                _logger.LogInformation("{name} @ {origin} with angles: {angle} is within zone {zonename}",
                                       ent.Name,
                                       ent.GetAbsOrigin(),
                                       ent.GetAbsAngles(),
                                       info.TargetName);

                break;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPointInBox(in Vector point, in Vector origin, float boundingRadius)
    {
        var mins = origin - boundingRadius;
        var maxs = origin + boundingRadius;

        return mins.X     <= point.X
               && point.X <= maxs.X
               && mins.Y  <= point.Y
               && point.Y <= maxs.Y
               && mins.Z  <= point.Z
               && point.Z <= maxs.Z;
    }

    private unsafe bool FindTrigger()
    {
        if (CreateTrigger != null)
        {
            return true;
        }

        var server = _bridge.Modules.Server;

        var strScript_CreateTrigger = server.FindString("Script_CreateTrigger");

        if (strScript_CreateTrigger == nint.Zero)
        {
            return false;
        }

        var ptr = server.FindPtr(strScript_CreateTrigger);

        if (ptr == nint.Zero)
        {
            return false;
        }

        var func = *(nint*) (ptr + 0x38);

        try
        {
            var reader  = new UnsafeCodeReader(func, 256);
            var decoder = Decoder.Create(64, reader, (ulong) func, DecoderOptions.AMD);

            while (reader.CanReadByte)
            {
                var instruction = decoder.Decode();

                if (instruction.IsInvalid)
                {
                    continue;
                }

                if (instruction is { Code: Code.Call_rel32_64, Op0Kind: OpKind.NearBranch64 })
                {
                    CreateTrigger = (delegate* unmanaged<Vector*, Vector*, Vector*, nint>) instruction.MemoryDisplacement64;

                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    private void SaveCustomZones()
    {
        var list = _zones.AsValueEnumerable().Where(i => i.Value.Prebuilt == false).Select(i => i.Value).ToArray();

        if (list.Length == 0)
        {
            return;
        }

        var path = Path.Combine(_zonePath, $"{_bridge.GlobalVars.MapName}.jsonc");

        try
        {
            var content = JsonSerializer.Serialize(list, Utils.SerializerOptions);

            File.WriteAllText(path, content);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to save zones to file {path}", path);
        }
    }

    private void LoadCustomZones()
    {
        var path = Path.Combine(_zonePath, $"{_bridge.GlobalVars.MapName}.jsonc");

        if (!File.Exists(path))
        {
            return;
        }

        var content = File.ReadAllText(path);

        try
        {
            var zones = JsonSerializer.Deserialize<List<ZoneInfo>>(content, Utils.DeserializerOptions);

            if (zones is null)
            {
                _logger.LogError("Failed to serialize zones from file {path}", path);

                return;
            }

            foreach (var zone in zones)
            {
                AddZone(zone);
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to load zones from file {path}", path);
        }
    }
}
