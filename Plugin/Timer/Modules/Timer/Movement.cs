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
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameObjects;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal partial class TimerModule
{
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

        if (_timerInfo[client.Slot] is not { } timerInfo || _stageTimerInfo[client.Slot] is not { } stageTimer)
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

        var inMainStartZone  = timerInfo.InZone  == EZoneType.Start;
        var inStageStartZone = stageTimer.InZone == EZoneType.Stage;

        if (!onGround && (inMainStartZone || inStageStartZone))
        {
            var maxJumps    = GetEffectiveMaxPrejumps(timerInfo.Track, _styleModule.GetStyleSetting(timerInfo.Style));
            var shouldBlock = false;

            // Check each timer independently based on which zone the player is in
            if (inMainStartZone
                && maxJumps >= 0
                && timerInfo.WasOnGround
                && timerInfo.OnGroundTick <= PrejumpGraceTicks
                && timerInfo.Jumps        >= maxJumps)
            {
                timerInfo.Jumps = 0;
                shouldBlock     = true;
            }

            if (inStageStartZone
                && maxJumps >= 0
                && stageTimer.WasOnGround
                && stageTimer.OnGroundTick <= PrejumpGraceTicks
                && stageTimer.Jumps        >= maxJumps)
            {
                stageTimer.Jumps = 0;
                shouldBlock      = true;
            }

            if (shouldBlock)
            {
                arg.Velocity      = new ();
                info->ForwardMove = 0;
                info->SideMove    = 0;
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

            timer.WasOnGround = onGround;
        }
    }

    private unsafe void OnPlayerProcessMovePost(IPlayerProcessMoveForwardParams arg)
    {
        if (!arg.Client.IsFakeClient)
        {
            _moves[arg.Client.Slot] = (arg.Info->ForwardMove, arg.Info->SideMove);
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient || arg.Pawn.AsPlayer() is not { IsAlive: true } pawn)
        {
            return;
        }

        var slot = client.Slot;

        if (_timerInfo[slot] is not { } timerInfo || _stageTimerInfo[slot] is not { } stageTimer)
        {
            return;
        }

        var mainRunning  = timerInfo.IsTimerRunning();
        var stageRunning = stageTimer.IsTimerRunning();

        if (!mainRunning && !stageRunning)
        {
            return;
        }

        var service  = arg.Service;
        var origin   = pawn.GetAbsOrigin();
        var angles   = pawn.GetEyeAngles();
        var velocity = pawn.GetAbsVelocity();

        var onGround = pawn.GroundEntityHandle.IsValid();
        bool? surfing = null;

        var (forwardMove, sideMove) = _moves[slot];

        var timescale = _styleModule.GetStyleSetting(timerInfo.Style).TimerScale;

        if (mainRunning)
        {
            var counted = timerInfo.Advance(timescale);

            UpdatePlayerStats(pawn, service, timerInfo, onGround, counted, origin, angles, velocity, forwardMove, sideMove, ref surfing);

            if (timerInfo.CurrentCheckpointInfo is { } currentCp)
            {
                if (counted)
                {
                    currentCp.AverageVelocity
                        += (velocity - currentCp.AverageVelocity) / (timerInfo.TimerTick - currentCp.TimerTick);
                }

                if (velocity.LengthSqr() > currentCp.MaxVelocity.LengthSqr())
                {
                    currentCp.MaxVelocity = velocity;
                }
            }

            timerInfo.LastYaw = angles.Y;
        }

        if (stageRunning)
        {
            var stageCounted = stageTimer.Advance(timescale);

            UpdatePlayerStats(pawn, service, stageTimer, onGround, stageCounted, origin, angles, velocity, forwardMove, sideMove, ref surfing);

            stageTimer.LastYaw = angles.Y;
        }
    }

    // A key reversed on either axis, as the SSJ panel counts them: A/D, W/S, or both at once for surf HSW.
    internal static bool IsStrafe(float forwardMove, float sideMove, float lastForwardMove, float lastSideMove)
        => (sideMove * lastSideMove) < 0 || (forwardMove * lastForwardMove) < 0;

    // Within ~10° of the velocity's line, the keys aren't strafing.
    private const float MinPushSin2 = 0.03f;

    /// <summary>
    ///     Which side of the velocity the strafe key pushes: 1 left, -1 right, 0 along it or none. Good sync turns that
    ///     way, whatever the style: a backwards player holding A turns right. A or D decides when held, so HSW's W+A
    ///     scores as A does, else W or S (sideways).
    /// </summary>
    internal static int PushSide(float yaw, Vector velocity, float forwardMove, float sideMove)
    {
        if (sideMove != 0)
        {
            forwardMove = 0;
        }

        var (sin, cos) = MathF.SinCos(yaw * (MathF.PI / 180f));

        // Forward is (cos, sin) and left (-sin, cos); a positive SideMove is left.
        var wishX = (forwardMove * cos) - (sideMove * sin);
        var wishY = (forwardMove * sin) + (sideMove * cos);
        var cross = (velocity.X * wishY) - (velocity.Y * wishX);
        var scale = ((velocity.X * velocity.X) + (velocity.Y * velocity.Y)) * ((wishX * wishX) + (wishY * wishY));

        return cross * cross <= MinPushSin2 * scale ? 0 : MathF.Sign(cross);
    }

    // a - b in [-180, 180), so turning through ±180° keeps its sign.
    internal static float YawDelta(float a, float b)
        => ((a - b + 540f) % 360f) - 180f;

    private void OnPlayerJump(IGameEvent e)
    {
        if (e.GetPlayerController("userid") is not { IsValidEntity: true } controller
            || _timerInfo[controller.PlayerSlot] is not { } timerInfo)
        {
            return;
        }

        timerInfo.Jumps++;

        if (_stageTimerInfo[controller.PlayerSlot] is { } stageTimer)
        {
            stageTimer.Jumps++;
        }
    }

    // LastForwardMove and LastLeftMove keep the last key held on each axis. Only a sync sample needs the surf trace,
    // done at most once a tick for both timers.
    private void UpdatePlayerStats(IPlayerPawn      pawn,
                                   IMovementService service,
                                   TimerInfo        timerInfo,
                                   bool             onGround,
                                   bool             counted,
                                   Vector           origin,
                                   Vector           angle,
                                   Vector           velocity,
                                   float            forwardMove,
                                   float            sideMove,
                                   ref bool?        surfing)
    {
        if (!onGround)
        {
            if (IsStrafe(forwardMove, sideMove, timerInfo.LastForwardMove, timerInfo.LastLeftMove))
            {
                timerInfo.Strafes++;
            }

            var yawDiff = YawDelta(angle.Y, timerInfo.LastYaw);

            if (MathF.Abs(yawDiff) > 0.01f
                && PushSide(angle.Y, velocity, forwardMove, sideMove) is var side and not 0
                && !IsSurfing(pawn, service, origin, ref surfing))
            {
                timerInfo.TotalMeasures++;

                if (side == MathF.Sign(yawDiff))
                {
                    timerInfo.GoodSync++;
                }
            }
        }

        if (forwardMove != 0)
        {
            timerInfo.LastForwardMove = forwardMove;
        }

        if (sideMove != 0)
        {
            timerInfo.LastLeftMove = sideMove;
        }

        if (velocity.LengthSqr() > timerInfo.MaxVelocity.LengthSqr())
        {
            timerInfo.MaxVelocity = velocity;
        }

        // Below timescale 1 not every tick counts (the first may not); the average follows the ones that do.
        if (counted)
        {
            timerInfo.AvgVelocity += (velocity - timerInfo.AvgVelocity) / timerInfo.TimerTick;
        }
    }

    // A ramp within reach below: sync isn't measured while surfing it.
    private bool IsSurfing(IPlayerPawn pawn, IMovementService service, Vector origin, ref bool? surfing)
    {
        if (surfing is { } known)
        {
            return known;
        }

        var hull = service.GetNetVar<bool>("m_bDucked") ? DuckedHull : StandingHull;

        var collision = pawn.GetCollisionProperty()!;

        var end = origin;
        end.Z -= SurfTraceDepth;

        var attribute = RnQueryShapeAttr.PlayerMovement(collision.CollisionAttribute.InteractsWith);
        attribute.SetEntityToIgnore(pawn, 0);

        var result = _bridge.PhysicsQueryManager.TraceShapePlayerMovement(new (hull), origin, end, attribute);

        surfing = result.DidHit() && Math.Abs(result.PlaneNormal.Z) < sv_standable_normal.GetFloat();

        return surfing.Value;
    }
}
