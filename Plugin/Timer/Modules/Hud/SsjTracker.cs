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
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer.Modules.Hud;

// Gain, sync and efficiency are fractions; gain goes below 0 when strafing against the velocity.
internal readonly record struct SsjStats(float SpeedDiff, float HeightDiff, float Gain, float Sync, int Strafes, float Efficiency);

// bhop-get-stats' colour tiers, worst to best.
internal enum SsjTier
{
    ReallyBad,
    Bad,
    Meh,
    Good,
    ReallyGood,
}

// The first jump of a chain has nothing before it to measure.
internal sealed record SsjJump(int Serial, int Chain, int Number, float Speed, SsjStats? Stats);

// A player's jumps for SSJ, counted the way bhop-get-stats counts them, but fed per movement step (ProcessMove runs
// once per sub-tick step) with each step weighted by its share of the tick. Gain follows CS2's AirAccelerate:
// wishspd = min(wishspeed, sv_air_max_wishspeed).
internal sealed class SsjTracker
{
    // This long on the ground (in ticks) and the next jump starts a new chain.
    public const float ChainGroundTicks = 10f;

    // Jumps kept for viewers that catch up once per HUD refresh.
    private const int Kept = 4;

    private readonly SsjJump?[] _recent = new SsjJump?[Kept];

    public int Jump   { get; private set; } // in the current chain; 0 once it ended
    public int Chain  { get; private set; }
    public int Serial { get; private set; } // jumps so far

    public bool InStep { get; private set; }

    private float _stepVx;
    private float _stepVy;
    private bool  _stepOnGround;
    private bool  _jumped;

    private float _ground;    // ticks on the ground since landing
    private int   _chainAir;  // air steps since the chain started
    private float _lastFwd;   // the last moves with a key held, for strafes
    private float _lastSide;

    // Since the last takeoff, over air steps.
    private float _air;
    private float _gain;
    private float _gaining;
    private float _trajectory;
    private float _dx;
    private float _dy;
    private int   _strafes;

    private float _takeoffSpeed;
    private float _takeoffZ;

    // The acceleration being scored, which steps split from it share.
    private long                       _accel = long.MinValue;
    private float                      _accelYaw;
    private float                      _accelFwd;
    private float                      _accelSide;
    private (float Gain, bool Synced)? _accelGain;

    // The oldest jump still kept after serial.
    public SsjJump? After(int serial)
    {
        for (var s = Math.Max(serial + 1, Serial - Kept + 1); s <= Serial; s++)
        {
            if (_recent[s % Kept] is { } jump && jump.Serial == s)
            {
                return jump;
            }
        }

        return null;
    }

    // Noclip, a ladder: the chain ends.
    public void Break()
    {
        InStep  = false;
        _jumped = false;
        EndChain();
    }

    public void BeginStep(float vx, float vy, bool onGround)
    {
        InStep        = true;
        _stepVx       = vx;
        _stepVy       = vy;
        _stepOnGround = onGround;
    }

    public void Jumped()
        => _jumped = true;

    // The moves are in units (after CheckParameters), side move positive to the left. A jump's own step still
    // counts toward the jump before it, and it takes off with the speed the step started with. Steps split from one
    // acceleration share its id and score once, from the velocity it started with.
    public SsjJump? EndStep(float weight, float yaw, float forwardMove, float sideMove, float airMaxWish, float z, long accel)
    {
        InStep = false;

        if (!float.IsFinite(weight) || weight <= 0f || weight > 1.5f)
        {
            weight = 1f;
        }

        SsjJump? takeoff = null;

        if (_jumped || !_stepOnGround)
        {
            _ground = 0;

            if (Jump > 0)
            {
                AirStep(weight, yaw, forwardMove, sideMove, airMaxWish, accel);
            }

            // A strafe is a key reversed on either axis; the chain's first air step has nothing to reverse.
            if (_chainAir > 0 && ((sideMove * _lastSide) < 0 || (forwardMove * _lastFwd) < 0))
            {
                _strafes++;
            }

            _chainAir++;

            if (_jumped)
            {
                _jumped = false;
                takeoff = Takeoff(MathF.Floor(MathF.Sqrt((_stepVx * _stepVx) + (_stepVy * _stepVy))), z);
            }
        }
        else
        {
            _ground += weight;

            if (_ground >= ChainGroundTicks && (Jump > 0 || _chainAir > 0))
            {
                EndChain();
            }
        }

        if (forwardMove != 0 || sideMove != 0)
        {
            _lastFwd  = forwardMove;
            _lastSide = sideMove;
        }

        return takeoff;
    }

    public static bool Shows(int number, int every, bool repeat, bool first)
        => (first && number == 1) || number == every || (repeat && every > 0 && number % every == 0);

    // 1 perpendicular to the velocity, 0 once the velocity along the wish direction reaches wishspd, below 0
    // against it. Synced is whether the step accelerates at all.
    public static (float Gain, bool Synced)? StepGain(float vx, float vy, float yaw, float forwardMove, float sideMove, float airMaxWish)
    {
        var (sin, cos) = MathF.SinCos(yaw * (MathF.PI / 180f));

        // CS2's AngleVectors, flattened: forward (cos, sin), left (-sin, cos).
        var wx = (cos * forwardMove) - (sin * sideMove);
        var wy = (sin * forwardMove) + (cos * sideMove);

        var wishSpeed = MathF.Sqrt((wx * wx) + (wy * wy));

        if (wishSpeed < 0.01f || !float.IsFinite(wishSpeed))
        {
            return null;
        }

        var wishSpd = MathF.Min(wishSpeed, airMaxWish);
        var along   = ((vx * wx) + (vy * wy)) / wishSpeed;

        return along < wishSpd ? ((wishSpd - MathF.Abs(along)) / wishSpd, true) : (0f, false);
    }

    private void AirStep(float weight, float yaw, float forwardMove, float sideMove, float airMaxWish, long accel)
    {
        var dt    = weight * TimerConstants.TickInterval;
        var speed = MathF.Sqrt((_stepVx * _stepVx) + (_stepVy * _stepVy));

        _air        += weight;
        _dx         += _stepVx * dt;
        _dy         += _stepVy * dt;
        _trajectory += speed * dt;

        if (accel != _accel || yaw != _accelYaw || forwardMove != _accelFwd || sideMove != _accelSide)
        {
            _accel     = accel;
            _accelYaw  = yaw;
            _accelFwd  = forwardMove;
            _accelSide = sideMove;
            _accelGain = StepGain(_stepVx, _stepVy, yaw, forwardMove, sideMove, MathF.Max(airMaxWish, 0.01f));
        }

        if (_accelGain is { } step)
        {
            _gain += weight * step.Gain;

            if (step.Synced)
            {
                _gaining += weight;
            }
        }
    }

    private SsjJump Takeoff(float speed, float z)
    {
        if (Jump == 0)
        {
            Chain++;
        }

        Jump++;
        Serial++;

        SsjStats? stats = null;

        if (Jump > 1 && _air > 0)
        {
            var gain     = _gain / _air;
            var straight = _trajectory > 0 ? MathF.Min(1f, MathF.Sqrt((_dx * _dx) + (_dy * _dy)) / _trajectory) : 0f;

            stats = new SsjStats(speed - _takeoffSpeed, z - _takeoffZ, gain, _gaining / _air, _strafes, gain * straight);
        }

        var jump = new SsjJump(Serial, Chain, Jump, speed, stats);
        _recent[Serial % Kept] = jump;

        _takeoffSpeed = speed;
        _takeoffZ     = z;
        ClearSegment();

        return jump;
    }

    private void EndChain()
    {
        Jump      = 0;
        _chainAir = 0;
        _accel    = long.MinValue;
        ClearSegment();
    }

    private void ClearSegment()
    {
        _air        = 0;
        _gain       = 0;
        _gaining    = 0;
        _trajectory = 0;
        _dx         = 0;
        _dy         = 0;
        _strafes    = 0;
    }
}
