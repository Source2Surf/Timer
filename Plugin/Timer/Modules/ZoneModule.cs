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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Iced.Intel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Zone;
using Source2Surf.Timer.Types;
using Source2Surf.Timer.Utilities;

namespace Source2Surf.Timer.Modules;

internal interface IZoneModule
{
    void RegisterListener(IZoneModuleListener listener);

    void UnregisterListener(IZoneModuleListener listener);

    void AddZone(ZoneInfo info);

    bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type);

    bool IsCurrentTrackLinear(int track);

    bool HasZone(int track, EZoneType type);

    bool CurrentTrackHasCheckpoints(int track);

    int GetTotalStages(int track);

    int GetCurrentTrackCheckpointCount(int track);

    // The highest checkpoint number; a map can have several triggers for one checkpoint.
    int GetLastCheckpoint(int track);

    bool TeleportToStage(IPlayerPawn pawn, int track, int stage);

    // Raised when an admin runs !zone without arguments, to open the zone editor.
    event Action<PlayerSlot>? EditorRequested;

    // Changes whenever a zone is added or deleted, or a zone being placed starts, moves on or ends.
    int EditVersion { get; }

    // Whether the player opened the editor with !zone; the editing calls below refuse everyone else.
    bool IsZoneEditor(PlayerSlot slot);

    void CloseZoneEditor(PlayerSlot slot);

    // Every zone, the map's own included, in display order.
    IReadOnlyList<ZoneEntry> GetZones();

    // The next free stage or checkpoint number on the track, 0 for other types.
    int NextZoneNumber(int track, EZoneType type);

    ZoneBuildState? GetZoneBuild(PlayerSlot slot);

    // Starts placing a zone; the player sets both corners with E.
    bool StartZoneBuild(PlayerSlot slot, int track, EZoneType type, int number);

    bool CancelZoneBuild(PlayerSlot slot);

    // Deletes a zone added in game and saves the rest; the map's own zones can't be deleted.
    bool DeleteZone(PlayerSlot slot, uint id);

    // Takes the player to any zone, the map's own included. Stops their run, so the zone can't start, split or finish it.
    bool TeleportToZone(PlayerSlot slot, uint id);

    // Whether this server's zones can turn and be resized (the trigger shape calls are found); they can always move.
    bool CanTurnZones { get; }

    // 自建区域按整图覆盖保存: 本图的还没加载齐(在加载, 或有一条没建出来)时不放开编辑, 免得保存冲掉没挂上的区域
    bool CanEditCustomZones { get; }

    // The zone added in game that the player edits, 0 for none.
    uint GetEditTarget(PlayerSlot slot);

    // Picks a zone added in game to edit; the map's own can't be.
    bool SelectZone(PlayerSlot slot, uint id);

    void DeselectZone(PlayerSlot slot);

    // The zone added in game the player stands in, 0 for none.
    uint ZoneAt(PlayerSlot slot);

    // Where a zone added in game is now; null for the map's own and unknown ids.
    ZonePlacement? GetPlacement(uint id);

    // Moves a zone added in game by so many units along the map's axes, and saves it.
    bool MoveZone(PlayerSlot slot, uint id, Vector delta);

    // Turns a zone added in game about its centre, anticlockwise from above, and saves it.
    bool TurnZone(PlayerSlot slot, uint id, float degrees);

    // Moves point 1 or 2 of a zone added in game along one of its box's own axes (0 X, 1 Y, 2 Z), the other point's
    // side staying, and saves it. Needs what turning does (CanTurnZones).
    bool ResizeZone(PlayerSlot slot, uint id, int point, int axis, float by);
}

// TODO:

internal partial class ZoneModule : IModule, IZoneModule, IEntityListener, IGameListener, IPlayerManagerListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IRequestManager      _requestManager;
    private readonly IPlayerManager       _playerManager;
    private readonly IMapInfoModule       _mapInfoModule;
    private readonly ILocalizationProvider _localization;

    private ITimerModule    _timerModule = null!;
    private IPlayerSettings _settings    = null!;

    private readonly ILogger<ZoneModule> _logger;
    private readonly ListenerHub<IZoneModuleListener> _listenerHub;

    private readonly int[] _currentMaxStages = new int[TimerConstants.MAX_TRACK];

    private readonly Dictionary<uint, ZoneInfo> _zones         = [];
    private readonly BuildZoneInfo?[]           _buildZoneInfo;

    private const int ZoneTypeCount = (int) EZoneType.Max;

    private readonly List<ZoneInfo>[,] _zonesByTrackType
        = new List<ZoneInfo>[TimerConstants.MAX_TRACK, ZoneTypeCount];

    private static readonly TraceShapeRay TeleportHull = new (new TraceShapeHull
    {
        Mins = new (-16, -16, 0),
        Maxs = new (16, 16, 72),
    });

    // How far below the zone's bottom the floor may be; under the ducked hull height so the player still touches it.
    private const float TeleportGroundMargin = 32f;

    // Landing flush with the floor gets the player stuck in it.
    private const float TeleportGroundLift = 2f;

    // ReSharper disable InconsistentNaming
    private unsafe delegate* unmanaged<Vector*, Vector*, Vector*, nint> CreateTrigger;

    // ReSharper restore InconsistentNaming

    public ZoneModule(InterfaceBridge       bridge,
                      ICommandManager       commandManager,
                      IRequestManager       requestManager,
                      IPlayerManager        playerManager,
                      ILocalizationProvider localization,
                      IMapInfoModule        mapInfoModule,
                      ILogger<ZoneModule>   logger)
    {
        _bridge            = bridge;
        _localization      = localization;
        _logger            = logger;
        _listenerHub       = new ListenerHub<IZoneModuleListener>(logger);
        _commandManager    = commandManager;
        _requestManager    = requestManager;
        _playerManager     = playerManager;
        _mapInfoModule     = mapInfoModule;
        _buildZoneInfo     = new BuildZoneInfo?[PlayerSlot.MaxPlayerCount];
        _lostZones         = [];

        timer_zone_solidify = bridge.ConVarManager.CreateConVar("timer_zone_solidify",
                                                                true,
                                                                "Make map zones built as hollow shells solid, from the next map");

        for (var t = 0; t < TimerConstants.MAX_TRACK; t++)
        {
            for (var z = 0; z < ZoneTypeCount; z++)
            {
                _zonesByTrackType[t, z] = [];
            }
        }
    }

    // A zone's turn as its trigger has it: none where zones can't turn.
    private float YawOf(ZoneInfo info)
        => _canTurn ? info.Yaw : 0f;

    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (entity.Classname != "trigger_multiple")
        {
            return;
        }

        var targetName = entity.Name;
        var handle     = entity.Handle.GetValue();

        if (_zones.ContainsKey(handle))
        {
            return;
        }

#if DEBUG
        _logger.LogInformation("Entity {classname} spawned with name {targetname}, handle: 0x{h:X}",
                               entity.Classname,
                               targetName,
                               entity.Handle.GetValue());
#endif

        if (string.IsNullOrEmpty(targetName) || string.IsNullOrWhiteSpace(targetName))
        {
            return;
        }

        // Skip zones created by the plugin itself
        if (targetName.StartsWith("surftimer_zone_"))
        {
            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Start) || AddPrebuiltZone(entity, targetName, EZoneType.End))
        {
#if DEBUG
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);
#endif

            entity.SetNetVar("m_flWait", TimerConstants.TickInterval);
            RebuildIfHollowLater(handle);

            return;
        }

        if (AddPrebuiltZone(entity, targetName, EZoneType.Stage) || AddPrebuiltZone(entity, targetName, EZoneType.Checkpoint))
        {
#if DEBUG
            _logger.LogInformation("Added prebuilt zone: {name}", targetName);
#endif
            entity.SetNetVar("m_flWait", TimerConstants.TickInterval);
            RebuildIfHollowLater(handle);

            return;
        }
#if DEBUG
        _logger.LogWarning("{t} is not the zone we want", targetName);
#endif
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        if (!_zones.Remove(entity.Handle.GetValue(), out var info))
            return;

        UnindexZone(info);
        NotifyZoneRemoved(info);

#if DEBUG
        _logger.LogWarning("Removed zone {t}", info.TargetName);
#endif

        if (info.Prebuilt)
        {
            return;
        }

        // 自建的 trigger_multiple 不在 modsharp 的回合保留名单里, 回合重启会被删掉; 地图自带的随地图实体重新生成
        if (_lostZones.Count == 0)
        {
            var generation = _zoneLoadGeneration;

            _bridge.ModSharp.InvokeFrameAction(() => RebuildLostZones(generation));
        }

        _lostZones.Add(info);
    }

    private void RebuildLostZones(int generation)
    {
        if (generation != _zoneLoadGeneration)
        {
            return;
        }

        foreach (var info in _lostZones)
        {
            try
            {
                AddZone(info);
            }
            catch (Exception e)
            {
                _customZonesLoaded = false;

                _logger.LogError(e,
                                 "Failed to rebuild zone (track={Track}, type={Type}); zones can't be edited until the next map.",
                                 info.Track,
                                 info.ZoneType);
            }
        }

        _lostZones.Clear();
        EditVersion++;
    }

    public EHookAction OnEntityFireOutput(IBaseEntity entity, string output, IBaseEntity? activator, float delay)
    {
        // Zone lookup first: this fires for every hooked output on every trigger_multiple
        // on the map, so non-zone triggers should exit on a dictionary miss before the
        // activator→pawn→controller resolution chain.
        if (!_zones.TryGetValue(entity.Handle.GetValue(), out var info))
        {
            return EHookAction.Ignored;
        }

        if (activator?.AsPlayerPawn() is not { IsValidEntity: true } pawn
            || pawn.GetController() is not { IsValidEntity : true } controller
            || controller.IsFakeClient)
        {
            return EHookAction.Ignored;
        }

        switch (output)
        {
            case "onstarttouch":
            {
                NotifyZoneStartTouch(info, controller, pawn);

                break;
            }
            case "onendtouch":
            {
                NotifyZoneEndTouch(info, controller, pawn);

                break;
            }
            case "ontrigger":
            {
                NotifyZoneTrigger(info, controller, pawn);

                break;
            }
        }

        return EHookAction.Ignored;
    }

    public void OnRoundRestarted()
    {
        FindZoneStartPosition();
    }

    public void OnGamePostInit()
    {
        if (!FindTrigger())
        {
            throw new InvalidOperationException("Failed to find CreateTriggerInternal");
        }

        FindShapeCalls();

        for (var i = 0; i < TimerConstants.MAX_TRACK; i++)
        {
            _currentMaxStages[i] = -1;
        }
    }

    public void OnGameShutdown()
    {
        for (var i = 0; i < TimerConstants.MAX_TRACK; i++)
        {
            _currentMaxStages[i] = -1;
        }

        // Abandon in-progress zone builds; clients drop the guide lines with the map, so only the references go.
        for (var slot = 0; slot < _buildZoneInfo.Length; slot++)
        {
            _buildZoneInfo[slot] = null;
        }

        Array.Fill(_zoneEditors, false);
        Array.Clear(_editTargets);

        _zoneLoadGeneration++;
        _customZonesLoaded = false;
        _lostZones.Clear();

        _zones.Clear();
        ZonesChanged();

        for (var t = 0; t < TimerConstants.MAX_TRACK; t++)
        {
            for (var z = 0; z < ZoneTypeCount; z++)
            {
                _zonesByTrackType[t, z].Clear();
            }
        }
    }

    public void OnServerActivate()
    {
        ForgetLines();
        LoadZoneOutlines();

        var mapName    = _bridge.CurrentMapName;
        var generation = ++_zoneLoadGeneration;

        _customZonesLoaded = false;

        Task.Run(async () =>
                 {
                     try
                     {
                         var zones = await RetryHelper.RetryAsync(() => _requestManager.GetZonesAsync(mapName),
                                                                  RetryHelper.IsTransient,
                                                                  _logger,
                                                                  "GetZonesAsync").ConfigureAwait(false);

                         await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                         {
                             if (generation != _zoneLoadGeneration)
                             {
                                 return;
                             }

                             var complete = true;

                             foreach (var zoneData in zones)
                             {
                                 var zoneInfo = ZoneMapper.ToZoneInfo(zoneData);

                                 if (!ZoneEdit.IsValid(zoneInfo.Track, zoneInfo.ZoneType, zoneInfo.Data))
                                 {
                                     _logger.LogWarning("Skipped stored zone (track={Track}, type={Type}, number={Number}): out of range",
                                                        zoneInfo.Track,
                                                        zoneInfo.ZoneType,
                                                        zoneInfo.Data);

                                     continue;
                                 }

                                 try
                                 {
                                     AddZone(zoneInfo);
                                 }
                                 catch (Exception e)
                                 {
                                     complete = false;

                                     // One broken zone must not abort loading the rest.
                                     _logger.LogError(e,
                                                      "Failed to create zone (track={Track}, type={Type}); skipping it.",
                                                      zoneInfo.Track,
                                                      zoneInfo.ZoneType);
                                 }
                             }

                             _customZonesLoaded = complete;
                             EditVersion++;
                         }).ConfigureAwait(false);
                     }
                     catch (Exception e)
                     {
                         _logger.LogError(e, "Failed to load custom zones from database");
                     }
                 },
                 _bridge.CancellationToken);
    }

    public bool Init()
    {
        // Only outputs the OnEntityFireOutput switch actually handles — OnTouching /
        // OnTouchingEachEntity fire per m_flWait per touching player on EVERY
        // trigger_multiple on the map and were dropped unhandled.
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnStartTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnEndTouch");
        _bridge.EntityManager.HookEntityOutput("trigger_multiple", "OnTrigger");

        _bridge.ModSharp.InstallGameListener(this);
        _bridge.EntityManager.InstallEntityListener(this);
        _playerManager.RegisterListener(this);

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommandPre);

        _commandManager.AddAdminChatCommand("zone", ["timer:zone"], OnCommandZone);
        _commandManager.AddClientChatCommand("showzones", OnCommandShowZones);
        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);

        return true;
    }

    // These depend on this module, so they're resolved after all are built.
    public void OnPostInit(ServiceProvider provider)
    {
        _timerModule = provider.GetRequiredService<ITimerModule>();
        _settings    = provider.GetRequiredService<IPlayerSettings>();
        _replay      = provider.GetRequiredService<IReplayModule>();
    }

    public void Shutdown()
    {
        _playerManager.UnregisterListener(this);
        _bridge.EntityManager.RemoveEntityListener(this);
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommandPre);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
        _listenerHub.Clear();
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        DropLines(slot);
        ClearBuildZoneInfo(slot);
        CloseZoneEditor(slot);
        _allTrackLines[slot] = false;
    }

    /// <summary>
    ///     Abandon a slot's in-progress zone build. Without this, the next player assigned
    ///     the slot would inherit an active build session (and its guide lines).
    /// </summary>
    private void ClearBuildZoneInfo(PlayerSlot slot)
    {
        if (_buildZoneInfo[slot] is not { } buildInfo)
        {
            return;
        }

        buildInfo.Lines.Clear();
        _buildZoneInfo[slot] = null;
    }

    public void RegisterListener(IZoneModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IZoneModuleListener listener)
        => _listenerHub.Unregister(listener);

    private void NotifyZoneStartTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
        => _listenerHub.NotifyAll("OnZoneStartTouch",
                                  static (l, i, c, p) => l.OnZoneStartTouch(i, c, p),
                                  info, controller, pawn);

    private void NotifyZoneEndTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
        => _listenerHub.NotifyAll("OnZoneEndTouch",
                                  static (l, i, c, p) => l.OnZoneEndTouch(i, c, p),
                                  info, controller, pawn);

    private void NotifyZoneTrigger(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
        => _listenerHub.NotifyAll("OnZoneTrigger",
                                  static (l, i, c, p) => l.OnZoneTrigger(i, c, p),
                                  info, controller, pawn);

    private void NotifyZoneRemoved(ZoneInfo info)
        => _listenerHub.NotifyAll("OnZoneRemoved",
                                  static (l, i) => l.OnZoneRemoved(i),
                                  info);

    public unsafe void AddZone(ZoneInfo info)
    {
        var targetName = $"surftimer_{info.Track}_{info.ZoneType}_{info.Data}".ToLowerInvariant();

        var (mins, maxs) = TriggerBounds(info);

        var origin = TriggerOrigin(info);

        var entPtr = CreateTrigger(&origin, &mins, &maxs);

        if (entPtr == 0)
        {
            throw new InvalidOperationException($"Failed to create trigger for zone (track={info.Track}, type={info.ZoneType})");
        }

        if (_bridge.EntityManager.MakeEntityFromPointer<IBaseTrigger>(entPtr) is not { } ent)
        {
            return;
        }

        ent.SetName(targetName);

        // SF_TRIGGER_ALLOW_CLIENTS (0x01) | SF_TRIG_TOUCH_DEBRIS (0x1000): trigger fires
        // for players, and keeps firing while they are surfing (debris-like movement).
        ent.SpawnFlags =  4097;
        ent.Effects    |= EntityEffects.NoDraw;
        ent.SetNetVar("m_flWait", TimerConstants.TickInterval);

        // Made SOLID_OBB, so that turning the trigger turns its box; unturned, it touches what a SOLID_BBOX one would.
        if (_canTurn)
        {
            SetShape(ent, SolidType.Obb);
        }

        if (YawOf(info) != 0f)
        {
            ent.Teleport(null, new Vector(0, info.Yaw, 0), null);
        }

        info.Origin = origin;
        info.Index  = ent.Index;

        if (_zones.TryAdd(ent.Handle.GetValue(), info))
        {
            IndexZone(info);
            RecountStages(info.Track);
        }
    }

    // Where a placed zone's trigger is: the middle of its box, which reaches out to both corners.
    private static Vector TriggerOrigin(ZoneInfo info)
        => (info.Corner1 + info.Corner2) / 2.0f;

    // A placed zone's trigger box about its origin, before its turn.
    private static (Vector Mins, Vector Maxs) TriggerBounds(ZoneInfo info)
    {
        var mins = new Vector();
        var maxs = new Vector();

        for (var i = 0; i < 3; i++)
        {
            maxs[i] = Math.Abs(info.Corner1[i] - info.Corner2[i]) / 2.0f;
            mins[i] = -maxs[i];
        }

        return (mins, maxs);
    }

    public bool TeleportToZone(IPlayerPawn pawn, int track, EZoneType type)
    {
        if (GetZoneBucket(track, type) is not { Count: > 0 } bucket)
        {
            return false;
        }

        pawn.Teleport(GetTeleportPosition(bucket[0], pawn), null, new Vector());

        return true;
    }

    public bool TeleportToStage(IPlayerPawn pawn, int track, int stage)
    {
        if (GetZoneBucket(track, EZoneType.Stage) is not { Count: > 0 } bucket)
        {
            return false;
        }

        foreach (var zoneInfo in bucket)
        {
            if (zoneInfo.Data != stage)
            {
                continue;
            }

            pawn.Teleport(GetTeleportPosition(zoneInfo, pawn), null, new Vector());

            return true;
        }

        return false;
    }

    // Without a set teleport point, land on the floor under the zone's center, else on its bottom like bhoptimer;
    // traced on first use.
    private Vector GetTeleportPosition(ZoneInfo info, IPlayerPawn pawn)
    {
        // A map config beats the map's own destination: some maps put their only one inside a booster.
        if (info.ZoneType == EZoneType.Start
            && _mapInfoModule.GetZoneTeleportOrigin(info.Track) is { } configured)
        {
            return configured;
        }

        if (info.TeleportOrigin is { } teleport)
        {
            return teleport;
        }

        if (info.GroundOrigin is { } ground)
        {
            return ground;
        }

        if (pawn.GetCollisionProperty() is not { } collision)
        {
            return info.Origin;
        }

        var bottom = Math.Min(info.Corner1.Z, info.Corner2.Z);
        var top    = Math.Max(info.Corner1.Z, info.Corner2.Z);

        var start = (info.Corner1 + info.Corner2) / 2.0f;
        start.Z = Math.Clamp(info.Origin.Z, bottom, top);

        ground   = start;
        ground.Z = bottom + TeleportGroundLift;

        var end = start;
        end.Z = bottom - TeleportGroundMargin;

        var attribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
        var result    = _bridge.PhysicsQueryManager.TraceShapeNoPlayers(TeleportHull, start, end, attribute);

        if (result.DidHit() && !result.StartInSolid)
        {
            ground   =  result.EndPosition;
            ground.Z += TeleportGroundLift;
        }

        info.GroundOrigin = ground;

        return ground;
    }

    public bool IsCurrentTrackLinear(int track)
        => _currentMaxStages[track] <= 1;

    public bool HasZone(int track, EZoneType type)
        => GetZoneBucket(track, type) is { Count: > 0 };

    public bool CurrentTrackHasCheckpoints(int track)
        => GetZoneBucket(track, EZoneType.Checkpoint) is { Count: > 0 };

    public int GetTotalStages(int track)
        => _currentMaxStages[track];

    public int GetCurrentTrackCheckpointCount(int track)
    {
        var numbers = 0UL;

        foreach (var zone in GetZoneBucket(track, EZoneType.Checkpoint) ?? [])
        {
            numbers |= 1UL << zone.Data;
        }

        return System.Numerics.BitOperations.PopCount(numbers);
    }

    public int GetLastCheckpoint(int track)
    {
        var last = 0;

        foreach (var zone in GetZoneBucket(track, EZoneType.Checkpoint) ?? [])
        {
            last = Math.Max(last, zone.Data);
        }

        return last;
    }

    private List<ZoneInfo>? GetZoneBucket(int track, EZoneType type)
    {
        var typeIndex = (int) type;

        if ((uint) track        >= TimerConstants.MAX_TRACK
            || (uint) typeIndex >= ZoneTypeCount)
        {
            return null;
        }

        return _zonesByTrackType[track, typeIndex];
    }

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
                    return ZoneMatcher.IsStartZone(targetName) && TryAddZoneToIndex(handle, info);
                }

                if ((uint) track >= TimerConstants.MAX_TRACK)
                {
                    return false;
                }

                if (_currentMaxStages[track] < 1)
                {
                    _currentMaxStages[track] = 1;
                }

                info.Track = track;

                return TryAddZoneToIndex(handle, info);
            }
            case EZoneType.End:
            {
                if (!ZoneMatcher.IsBonusEndZone(targetName, out track))
                {
                    return ZoneMatcher.IsEndZone(targetName) && TryAddZoneToIndex(handle, info);
                }

                info.Track = track;

                return TryAddZoneToIndex(handle, info);
            }
            case EZoneType.Stage:
            {
                if (!ZoneMatcher.IsStageZone(targetName, out var stage) || !NumberInRange(targetName, EZoneType.Stage, stage))
                {
                    return false;
                }

                if (stage >= _currentMaxStages[0])
                {
                    _currentMaxStages[0] = stage;
                }

                info.Data = stage;

                return TryAddZoneToIndex(handle, info);
            }
            case EZoneType.Checkpoint:
            {
                if (ZoneMatcher.IsCheckpointZone(targetName, out var cp))
                {
                    info.Data = cp;

                    return NumberInRange(targetName, EZoneType.Checkpoint, cp) && TryAddZoneToIndex(handle, info);
                }

                if (ZoneMatcher.IsBonusCheckpointZone(targetName, out var bonusTrack, out cp))
                {
                    info.Track = bonusTrack;
                    info.Data  = cp;

                    return NumberInRange(targetName, EZoneType.Checkpoint, cp) && TryAddZoneToIndex(handle, info);
                }

                break;
            }
        }

        return false;
    }

    private bool NumberInRange(string name, EZoneType type, int number)
    {
        if (number <= ZoneEdit.LastNumber(type))
        {
            return true;
        }

        _logger.LogWarning("Map trigger {Name} is numbered {Number}, past what a run can submit; not used as a zone", name, number);

        return false;
    }

    private bool TryAddZoneToIndex(uint handle, ZoneInfo info)
    {
        if (!_zones.TryAdd(handle, info))
        {
            return false;
        }

        IndexZone(info);
        return true;
    }

    private void IndexZone(ZoneInfo info)
    {
        var typeIndex = (int) info.ZoneType;

        if (typeIndex is < 0 or >= ZoneTypeCount)
        {
            return;
        }

        var track = info.Track;

        if ((uint) track >= TimerConstants.MAX_TRACK)
        {
            return;
        }

        _zonesByTrackType[track, typeIndex].Add(info);
        ZonesChanged();
    }

    private void UnindexZone(ZoneInfo info)
    {
        var typeIndex = (int) info.ZoneType;

        if (typeIndex is < 0 or >= ZoneTypeCount)
        {
            return;
        }

        var track = info.Track;

        if ((uint) track >= TimerConstants.MAX_TRACK)
        {
            return;
        }

        _zonesByTrackType[track, typeIndex].Remove(info);
        ZonesChanged();
    }

    // The zone's outline: a manual zone's box, or a prebuilt trigger's mesh perimeter.
    private List<Edge> GetEdges(ZoneInfo val)
    {
        var edgesToDraw = new List<Edge>();

        if (val.Prebuilt)
        {
            if (_bridge.EntityManager.FindEntityByIndex<IBaseModelEntity>(val.Index) is { } entity)
            {
                var modelPath = entity.GetBodyComponent()?.GetSceneNode()?.AsSkeletonInstance?.GetModelState().ModelName ?? "";

                var kv3 = _bridge.ModSharp.CreateKeyValues3(KeyValues3Type.Null, KeyValues3SubType.Null);

                try
                {
                    if (kv3.LoadFromCompiledFile(modelPath, "GAME", ResourceBlockType.PHYS, out var error))
                    {
                        var localEdges = MeshPerimeterExtractor.ExtractPerimeterEdges(kv3, true);

                        var origin = entity.GetAbsOrigin();

                        foreach (var edge in localEdges)
                        {
                            var worldV1 = edge.V1 + origin;
                            var worldV2 = edge.V2 + origin;

                            edgesToDraw.Add(new Edge(worldV1, worldV2));
                        }
                    }
                }
                catch (Exception e)
                {
                    edgesToDraw.Clear();
                    _logger.LogWarning(e, "Failed to read the outline of zone {Zone}, it is not drawn", val.TargetName);
                }
                finally
                {
                    kv3.DeleteThis();
                }
            }
        }
        else
        {
            // A placed zone's box, turned as its trigger is: around the bottom, around the top, and the 4 uprights.
            var corners = ZoneEdit.Corners(val.Corner1, val.Corner2, YawOf(val));

            for (var i = 0; i < 4; i++)
            {
                var next = (i + 1) % 4;

                edgesToDraw.Add(new Edge(corners[i], corners[next]));
                edgesToDraw.Add(new Edge(corners[i + 4], corners[next + 4]));
                edgesToDraw.Add(new Edge(corners[i], corners[i + 4]));
            }
        }

        return edgesToDraw;
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

#if DEBUG
                _logger.LogInformation("{name} @ {origin} with angles: {angle} is within zone {zonename}",
                                       ent.Name,
                                       ent.GetAbsOrigin(),
                                       ent.GetAbsAngles(),
                                       info.TargetName);
#endif

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

        if (func == nint.Zero)
        {
            return false;
        }

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
        catch (Exception e)
        {
            _logger.LogWarning(e, "Error while decoding instructions in FindTrigger");
        }

        return false;
    }
}
