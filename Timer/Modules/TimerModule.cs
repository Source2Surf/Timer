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
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using SurfTimer.Extensions;
using SurfTimer.Managers;
using SurfTimer.Managers.Player;
using SurfTimer.Modules.Timer;
using SurfTimer.Modules.Zone;

namespace SurfTimer.Modules;

internal interface ITimerModule
{
    delegate void PlayerFinishMapDelegate(IPlayerController controller,
                                          IPlayerPawn       pawn,
                                          ITimerInfo        timerInfo);

    delegate void PlayerFinishStageTimerDelegate(IPlayerController controller,
                                                 IPlayerPawn       pawn,
                                                 IStageTimerInfo   stageTimerInfo);

    delegate void PlayerTimeStartDelegate(IPlayerController controller,
                                          IPlayerPawn       pawn,
                                          ITimerInfo        timerInfo);

    delegate void PlayerOnStageTimerStartDelegate(IPlayerController controller,
                                                  IPlayerPawn       pawn,
                                                  IStageTimerInfo   stageTimerInfo);

    event PlayerTimeStartDelegate OnPlayerTimerStart;
    event PlayerFinishMapDelegate OnPlayerFinishMap;

    event PlayerOnStageTimerStartDelegate OnPlayerStageTimerStart;
    event PlayerFinishStageTimerDelegate  OnPlayerStageTimerFinish;

    ITimerInfo? GetTimerInfo(PlayerSlot slot);

    ITimerInfo? GetStageTimerInfo(PlayerSlot slot);

    void StopTimer(PlayerSlot slot);
}

// TODO:
// WRCP time

internal partial class TimerModule : ITimerModule, IModule
{
    private readonly InterfaceBridge      _bridge;
    private readonly ICommandManager      _commandManager;
    private readonly IEventHookManager    _eventHook;
    private readonly ILogger<TimerModule> _logger;

    private readonly IPlayerManager _playerManager;

    private static readonly TimerInfo?[]      TimerInfo;
    private static readonly StageTimerInfo?[] StageTimerInfo;

    private readonly IZoneModule _zoneModule;

    // ReSharper disable InconsistentNaming
    private readonly IConVar timer_max_prejump;
    private readonly IConVar timer_max_prespeed;
    private readonly IConVar timer_max_prespeed_z;

    private readonly IConVar sv_standable_normal;

    // ReSharper restore InconsistentNaming

    static TimerModule()
    {
        TimerInfo = Enumerable.Repeat<TimerInfo?>(null, PlayerSlot.MaxPlayerSlot)
                              .ToArray();

        StageTimerInfo = Enumerable.Repeat<StageTimerInfo?>(null, PlayerSlot.MaxPlayerSlot)
                                   .ToArray();
    }

    public TimerModule(InterfaceBridge      bridge,
                       IPlayerManager       playerManager,
                       IZoneModule          zoneModule,
                       IEventHookManager    eventHook,
                       ICommandManager      commandManager,
                       ILogger<TimerModule> logger)
    {
        _bridge         = bridge;
        _playerManager  = playerManager;
        _zoneModule     = zoneModule;
        _eventHook      = eventHook;
        _commandManager = commandManager;

        _logger = logger;

        sv_standable_normal = bridge.ConVarManager.FindConVar("sv_standable_normal")!;

        timer_max_prejump    = bridge.ConVarManager.CreateConVar("timer_max_prejump",    1,    1, 10, "可以在起点里跳多少次")!;
        timer_max_prespeed   = bridge.ConVarManager.CreateConVar("timer_max_prespeed",   375f, "出起点时的最大2D速度")!;
        timer_max_prespeed_z = bridge.ConVarManager.CreateConVar("timer_max_prespeed_z", 500f, 0, 3500f, "出起点时的最大垂直速度")!;

        // ReSharper disable InconsistentNaming
        if (bridge.ConVarManager.FindConVar("view_punch_decay") is { } view_punch_decay)
        {
            view_punch_decay.Flags &= ~ConVarFlags.Cheat;

            view_punch_decay.Set(1000000f);
        }

        if (bridge.ConVarManager.FindConVar("sv_suppress_viewpunch", true) is { } sv_suppress_viewpunch)
        {
            sv_suppress_viewpunch.Flags &= ~ConVarFlags.DevelopmentOnly;

            sv_suppress_viewpunch.Set(1);
        }

        // ReSharper restore InconsistentNaming
    }

    public bool Init()
    {
        _eventHook.ListenEvent("player_jump", OnPlayerJump);
        _eventHook.HookEvent("player_team", hk_OnPlayerJoinTeam);

        _bridge.HookManager.HandleCommandJoinTeam.InstallHookPost(OnHandleCommandJoinTeamPost);

        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);
        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);

        _zoneModule.OnStartTouch += OnZoneStartTouch;
        _zoneModule.OnTrigger    += OnZoneTrigger;
        _zoneModule.OnEndTouch   += OnZoneEndTouch;

        _playerManager.ClientPutInServer  += OnClientPutInServer;
        _playerManager.ClientDisconnected += OnClientDisconnected;

        InitCommands();

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
    }

    public void Shutdown()
    {
        _bridge.HookManager.HandleCommandJoinTeam.RemoveHookPost(OnHandleCommandJoinTeamPost);

        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPlayerProcessMovePre);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);

        _zoneModule.OnStartTouch -= OnZoneStartTouch;
        _zoneModule.OnTrigger    -= OnZoneTrigger;
        _zoneModule.OnEndTouch   -= OnZoneEndTouch;

        _playerManager.ClientPutInServer  -= OnClientPutInServer;
        _playerManager.ClientDisconnected -= OnClientDisconnected;
    }

    private void OnZoneStartTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (!pawn.IsAlive || controller.IsFakeClient)
        {
            return;
        }

        if (TimerInfo[controller.PlayerSlot] is not { } timerInfo
            || StageTimerInfo[controller.PlayerSlot] is not { } stageTimer)
        {
            return;
        }

        if (info.Track != timerInfo.Track)
        {
            return;
        }

        timerInfo.UpdateInZone(info.ZoneType);

        var velocity = pawn.GetAbsVelocity();

        switch (info.ZoneType)
        {
            case EZoneType.Start:
            {
                timerInfo.StopTimer();

                break;
            }
            case EZoneType.End:
            {
                if (stageTimer.IsTimerRunning())
                {
                    stageTimer.EndVelocity = velocity;
                    OnPlayerStageTimerFinish?.Invoke(controller, pawn, stageTimer);
                    stageTimer.StopTimer();
                }

                if (timerInfo.IsTimerRunning())
                {
                    timerInfo.EndVelocity = velocity;

                    if (_zoneModule.CurrentTrackHasCheckpoints(timerInfo.Track)
                        && timerInfo.CurrentCheckpointInfo is { } currentCp)
                    {
                        currentCp.Sync = timerInfo.Sync;
                        timerInfo.AddCheckpoint(currentCp);
                    }

                    OnPlayerFinishMap?.Invoke(controller, pawn, timerInfo);

                    timerInfo.StopTimer();
                }

                break;
            }
            case EZoneType.Stage:
            {
                if (!stageTimer.IsTimerRunning())
                {
                    return;
                }

                var newStageIndex = info.Data;

                // going backwards or skipping stages
                if (newStageIndex <= stageTimer.Stage || newStageIndex != stageTimer.Stage + 1)
                {
                    timerInfo.StopTimer();
                    stageTimer.StopTimer();

                    controller.PrintToChat("Missing stages, stopping timer");

                    return;
                }

                stageTimer.EndVelocity = velocity;

                OnPlayerStageTimerFinish?.Invoke(controller, pawn, stageTimer);

                stageTimer.StopTimer();
                stageTimer.Stage = newStageIndex;

                break;
            }
            case EZoneType.Checkpoint:
            {
                if (!timerInfo.IsTimerRunning())
                {
                    return;
                }

                if (timerInfo.CurrentCheckpointInfo is not { } checkpointInfo)
                {
                    return;
                }

                var newCheckpointIndex = info.Data;

                // going backwards or touching the same checkpoint? 
                if (newCheckpointIndex <= timerInfo.Checkpoint)
                {
                    return;
                }

                if (newCheckpointIndex != timerInfo.Checkpoint + 1)
                {
                    timerInfo.StopTimer();
                    pawn.PrintToChat("Timer stopped: missing checkpoints");

                    return;
                }

                checkpointInfo.TimerTick   = timerInfo.TimerTick;
                checkpointInfo.EndVelocity = velocity;
                checkpointInfo.Sync        = timerInfo.Sync;

                timerInfo.AddCheckpoint(checkpointInfo);

                timerInfo.CurrentCheckpointInfo = new ();

                timerInfo.Checkpoint = newCheckpointIndex;
                pawn.PrintToChat($"{ChatColor.LightGreen}{controller.PlayerName}{ChatColor.White} reached CP{info.Data} with time {ChatColor.LightGreen}{Utils.FormatTime(timerInfo.Time, true)}{ChatColor.White}");

                break;
            }
            case EZoneType.StopTimer:
            {
                timerInfo.StopTimer();
                stageTimer.StopTimer();

                break;
            }
        }
    }

    private void OnZoneTrigger(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (TimerInfo[controller.PlayerSlot] is not { } timerInfo
            || StageTimerInfo[controller.PlayerSlot] is not { } stageTimer
            || !pawn.IsAlive
            || controller.IsFakeClient)
        {
            return;
        }

        if (info.ZoneType == EZoneType.StopTimer)
        {
            // should never happen but just in case
            if (info.Track == timerInfo.Track && timerInfo.IsTimerRunning())
            {
                timerInfo.StopTimer();
            }

            if (info.Track == stageTimer.Track && stageTimer.IsTimerRunning())
            {
                stageTimer.StopTimer();
            }
        }

        timerInfo.UpdateInZone(info.ZoneType);
        stageTimer.UpdateInZone(info.ZoneType);
    }

    private void OnZoneEndTouch(ZoneInfo info, IPlayerController controller, IPlayerPawn pawn)
    {
        if (TimerInfo[controller.PlayerSlot] is not { } timerInfo
            || StageTimerInfo[controller.PlayerSlot] is not { } stageTimer
            || !pawn.IsAlive
            || controller.IsFakeClient)
        {
            return;
        }

        if (info.Track != timerInfo.Track)
        {
            return;
        }

        timerInfo.UpdateInZone(EZoneType.Invalid);

        var velocity = pawn.GetAbsVelocity();
        var speed    = velocity.Length2D();

        switch (info.ZoneType)
        {
            case EZoneType.Start:
            {
                if (!_zoneModule.HasZone(timerInfo.Track, EZoneType.End))
                {
                    return;
                }

                // TODO: Zone prespeed from ZoneInfo
                var maxPreSpeed         = timer_max_prespeed.GetFloat();
                var maxVerticalPreSpeed = timer_max_prespeed_z.GetFloat();

                var scale        = maxPreSpeed / Math.Max(speed, 1);
                var shouldUpdate = false;

                if (scale < 1.0f)
                {
                    velocity.X   *= scale;
                    velocity.Y   *= scale;
                    shouldUpdate =  true;
                }

                if (velocity.Z > maxVerticalPreSpeed)
                {
                    velocity.Z   = maxVerticalPreSpeed;
                    shouldUpdate = true;
                }

                if (shouldUpdate)
                {
                    _bridge.ModSharp.InvokeFrameAction(() => { pawn.SetAbsVelocity(velocity); });
                }

                if (_zoneModule.CurrentTrackHasCheckpoints(timerInfo.Track))
                {
                    timerInfo.CurrentCheckpointInfo = new ()
                    {
                        StartVelocity = velocity,
                    };
                }
                else
                {
                    timerInfo.CurrentCheckpointInfo = null;
                }

                timerInfo.StartTimer(info.Track, velocity);
                OnPlayerTimerStart?.Invoke(controller, pawn, timerInfo);

                if (_zoneModule.IsCurrentTrackLinear(timerInfo.Track))
                {
                    return;
                }

                stageTimer.StartTimer(info.Track, velocity, 1);
                OnPlayerStageTimerStart?.Invoke(controller, pawn, stageTimer);

                break;
            }
            case EZoneType.Stage:
            {
                if (_zoneModule.IsCurrentTrackLinear(timerInfo.Track) || stageTimer.Stage != info.Data)
                {
                    return;
                }

                stageTimer.StartTimer(info.Track, velocity, info.Data);
                OnPlayerStageTimerStart?.Invoke(controller, pawn, stageTimer);

                /*
                if (!timerInfo.IsTimerRunning())
                {
                    return;
                }

                var stageTimer = timerInfo.StageTimer;
                stageTimer.Start(velocity);
                OnPlayerStageTimerStart?.Invoke(controller, pawn, timerInfo, stageTimer);
                */

                break;
            }
        }
    }

    public event ITimerModule.PlayerFinishMapDelegate?         OnPlayerFinishMap;
    public event ITimerModule.PlayerOnStageTimerStartDelegate? OnPlayerStageTimerStart;
    public event ITimerModule.PlayerTimeStartDelegate?         OnPlayerTimerStart;
    public event ITimerModule.PlayerFinishStageTimerDelegate?  OnPlayerStageTimerFinish;

    public ITimerInfo? GetTimerInfo(PlayerSlot slot)
        => TimerInfo[slot];

    public ITimerInfo? GetStageTimerInfo(PlayerSlot slot)
        => StageTimerInfo[slot];

    public void StopTimer(PlayerSlot slot)
    {
        if (TimerInfo[slot] is { } timerInfo)
        {
            timerInfo.StopTimer();
        }

        if (StageTimerInfo[slot] is { } stageTimer)
        {
            stageTimer.StopTimer();
        }
    }

    private static HookReturnValue<bool> hk_OnPlayerJoinTeam(EventHookParams arg)
        => new (EHookAction.SkipCallReturnOverride);

    private static void OnHandleCommandJoinTeamPost(IHandleCommandJoinTeamHookParams @params, HookReturnValue<bool> hook)
    {
        if (@params.Team == 1)
        {
            return;
        }

        @params.Controller.Respawn();
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams arg)
    {
        var client = arg.Client;

        var pawn = arg.Pawn;

        if (TimerInfo[client.Slot] is { } timerInfo)
        {
            _zoneModule.TeleportToZone(pawn, timerInfo.Track, EZoneType.Start);
        }
        else
        {
            _zoneModule.TeleportToZone(pawn, 0, EZoneType.Start);
        }
    }

    private void Restart(IGamePlayer player, int track = -1)
    {
        if (player.Controller is not { } controller)
        {
            return;
        }

        if (controller.GetPlayerPawn() is not { IsValidEntity: true } pawn)
        {
            return;
        }

        if (!pawn.IsAlive)
        {
            return;
        }

        if (TimerInfo[player.Slot] is { } timerInfo)
        {
            timerInfo.StopTimer();
            timerInfo.ChangeTrack(track);
        }

        if (StageTimerInfo[player.Slot] is { } stageTimer)
        {
            stageTimer.StopTimer();
            stageTimer.ChangeTrack(track);
        }

        if (!_zoneModule.TeleportToZone(pawn, track, EZoneType.Start))
        {
            return;
        }
    }

    private static void OnClientPutInServer(IGamePlayer player)
    {
        if (player.IsFakeClient)
        {
            return;
        }

        TimerInfo[player.Slot]      = new ();
        StageTimerInfo[player.Slot] = new ();
    }

    private static void OnClientDisconnected(IGamePlayer player)
    {
        if (player.IsFakeClient)
        {
            return;
        }

        TimerInfo[player.Slot]      = null;
        StageTimerInfo[player.Slot] = null;
    }

    private unsafe void OnPlayerProcessMovePre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsValid || client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var service = arg.Service;

        service.Stamina   = 0f;
        service.DuckSpeed = 7.0f;

        if (TimerInfo[client.Slot] is not { } timerInfo || StageTimerInfo[client.Slot] is not { } stageTimer)
        {
            return;
        }

        var onGround = pawn.GroundEntityHandle.IsValid();

        var info = arg.Info;

        if (pawn.ActualMoveType == MoveType.NoClip)
        {
            timerInfo.StopTimer();
            stageTimer.StopTimer();

            timerInfo.OnGroundTick  = 0;
            stageTimer.OnGroundTick = 0;

            return;
        }

        var forwardmove = info->ForwardMove;
        var sidemove    = info->SideMove;

        var isInStartZone         = timerInfo.InZone == EZoneType.Start || stageTimer.InZone == EZoneType.Stage;
        var wasOnGround           = timerInfo.WasOnGround               || stageTimer.WasOnGround;
        var justLeftGround        = !onGround && wasOnGround;
        var isWithinPrejumpWindow = timerInfo.OnGroundTick <= 10 || stageTimer.OnGroundTick <= 10;

        if (isInStartZone && justLeftGround && isWithinPrejumpWindow)
        {
            var maxJumps = timer_max_prejump.GetInt32();

            if (timerInfo.Jumps >= maxJumps || stageTimer.Jumps >= maxJumps)
            {
                arg.Velocity      = new ();
                info->ForwardMove = 0;
                info->SideMove    = 0;

                timerInfo.Jumps  = 0;
                stageTimer.Jumps = 0;
            }
        }

        UpdateTimerState(timerInfo);
        UpdateTimerState(stageTimer);

        return;

        void UpdateTimerState(TimerInfo timer)
        {
            if (onGround)
            {
                timer.OnGroundTick++;
            }
            else
            {
                timer.OnGroundTick = 0;
            }

            timer.WasOnGround     = onGround;
            timer.LastForwardMove = forwardmove;
            timer.LastLeftMove    = sidemove;
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive || TimerInfo[client.Slot] is not { } timerInfo || StageTimerInfo[client.Slot] is not { } stageTimer)
        {
            return;
        }

        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        var service = arg.Service;

        var leftmove = service.GetNetVarOffset("m_flLeftMove");

        var collision = pawn.GetCollisionProperty()!;

        var hull = new TraceShapeHull
        {
            Mins = new (-16, -16, 0),
            Maxs = new (16, 16, service.GetNetVar<bool>("m_bDucked") ? 54 : 72),
        };

        var origin = pawn.GetAbsOrigin();
        var end    = origin;
        end.Z -= 54;

        var attribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
        attribute.SetEntityToIgnore(pawn, 0);

        var result = _bridge.PhysicsQueryManager.TraceShapePlayerMovement(new (hull),
                                                                          origin,
                                                                          end,
                                                                          attribute);

        var isSurfing = result.DidHit() && Math.Abs(result.PlaneNormal.Z) < sv_standable_normal.GetFloat();

        if (timerInfo.IsTimerRunning())
        {
            timerInfo.TimerTick++;

            UpdatePlayerStats(pawn,
                              service,
                              timerInfo,
                              angles,
                              velocity,
                              isSurfing,
                              leftmove,
                              timerInfo.LastYaw);

            if (timerInfo.CurrentCheckpointInfo is { } currentCp)
            {
                currentCp.AverageVelocity
                    += (velocity - currentCp.AverageVelocity) / (timerInfo.TimerTick - currentCp.TimerTick);
            }

            timerInfo.LastYaw = angles.Y;
        }

        if (stageTimer.IsTimerRunning())
        {
            stageTimer.TimerTick++;

            UpdatePlayerStats(pawn,
                              service,
                              stageTimer,
                              angles,
                              velocity,
                              isSurfing,
                              leftmove,
                              stageTimer.LastYaw);

            stageTimer.LastYaw = angles.Y;
        }
    }

    private static void OnPlayerJump(IGameEvent e)
    {
        if (e.GetPlayerController("userid") is not { IsValidEntity: true } controller
            || TimerInfo[controller.PlayerSlot] is not { } timerInfo)
        {
            return;
        }

        timerInfo.Jumps++;

        if (StageTimerInfo[controller.PlayerSlot] is { } stageTimer)
        {
            stageTimer.Jumps++;
        }
    }

    private static void UpdatePlayerStats(IPlayerPawn      pawn,
                                          IMovementService service,
                                          TimerInfo        timerInfo,
                                          Vector           angle,
                                          Vector           velocity,
                                          bool             isSurfing,
                                          float            sidemove,
                                          float            lastYaw)
    {
        var onGround = pawn.GroundEntityHandle.IsValid();

        if (!onGround)
        {
            var yawDiff = angle.Y - lastYaw;

            if (timerInfo.LastLeftMove != 0 && sidemove is > 0 or < 0)
            {
                timerInfo.Strafes++;
            }

            var buttons = service.KeyButtons;

            var isPressingLeft  = (buttons & UserCommandButtons.MoveLeft)  != 0;
            var isPressingRight = (buttons & UserCommandButtons.MoveRight) != 0;

            if (!isSurfing && (isPressingLeft || isPressingRight) && MathF.Abs(yawDiff) > 0.01)
            {
                timerInfo.TotalMeasures++;

                if ((yawDiff    > 0.0f && isPressingLeft  && !isPressingRight)
                    || (yawDiff < 0.0f && !isPressingLeft && isPressingRight))
                {
                    timerInfo.GoodSync++;
                }
            }
        }

        if (velocity.LengthSqr() > timerInfo.MaxVelocity.LengthSqr())
        {
            timerInfo.MaxVelocity = velocity;
        }

        timerInfo.AvgVelocity += (velocity - timerInfo.AvgVelocity) / timerInfo.TimerTick;
    }
}
