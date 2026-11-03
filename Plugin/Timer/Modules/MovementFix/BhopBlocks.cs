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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Native;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// A player's bhop platforms as a saveloc keeps them: how long each one they had set moving had been moving, and what
// they stood on.
internal sealed record BhopBlockSnapshot(uint Ground, (uint Block, float Elapsed)[] Moving);

// mpbhops_but_working, on bhop maps. A bhop platform (a func_door or func_button a touch moves) moves for everyone, so
// it stays still instead, and moves for each player on their own clock, as if they alone had set it off: it goes down or
// up, waits, and comes back. Each player gets what that would have done to them: a teleport once a dropping one has sunk
// them into its trigger_teleport, standing on it or landing on it while it's down, and a rising one's speed when they
// jump off it while it rises.
internal unsafe partial class MovementFixModule
{
    private const uint SF_DOOR_PTOUCH            = 1024;
    private const uint SF_DOOR_SILENT            = 4096;
    private const uint SF_BUTTON_DONTMOVE        = 1;
    private const uint SF_BUTTON_TOUCH_ACTIVATES = 256;

    // By handle: a platform kept still, and how it would have moved. It takes MoveTime to go Travel units down or up,
    // stays there Wait (for good when negative), and takes as long to come back. Teleport is the trigger_teleport a
    // dropping one sinks the player into once it has sunk Depth; 0 for a rising one.
    private readonly record struct BhopBlock(uint Teleport, float Depth, float Travel, float Speed, float MoveTime, float Wait)
    {
        // Back where it started, so a touch sets it off again.
        public bool AtRest(float elapsed)
            => Wait >= 0.0f && elapsed >= MoveTime + Wait + MoveTime;

        // How far it has moved from where it started.
        public float Offset(float elapsed)
        {
            if (elapsed < MoveTime)
            {
                return Travel * elapsed / MoveTime;
            }

            if (Wait < 0.0f || elapsed < MoveTime + Wait)
            {
                return Travel;
            }

            return MathF.Max(Travel * (1.0f - (elapsed - MoveTime - Wait) / MoveTime), 0.0f);
        }
    }

    private enum BlockVerdict
    {
        Dropping,
        Rising,
        NotTouched,
        NoTeleport,
        Still,
        NoReturn,
    }

    private static readonly Dictionary<uint, BhopBlock> _bhopBlocks    = [];
    private static readonly List<uint>                  _pendingBlocks = [];
    private static readonly HashSet<uint>               _inspected     = [];
    private static          bool                        _blocksActive;
    private static          bool                        _blocksQueued;

    // By slot: when (CurTime) each platform the player set off started moving for them, by its handle.
    private static readonly Dictionary<uint, float>?[] _blockStarts = new Dictionary<uint, float>?[PlayerSlot.MaxPlayerCount];
    private static readonly uint[]                     _lastGround  = new uint[PlayerSlot.MaxPlayerCount];

    private static IGlobalVars? _globalVars;

    // The map's mode is known from its activation on, and every teleport has spawned by the frame after it.
    public void OnGameActivate()
        => _bridge.ModSharp.InvokeFrameAction(() =>
        {
            _blocksActive = true;
            _pendingBlocks.Clear();

            foreach (var classname in (ReadOnlySpan<string>) ["func_door", "func_button"])
            {
                IBaseEntity? entity = null;

                while ((entity = _entityManager.FindEntityByClassname(entity, classname)) != null)
                {
                    _pendingBlocks.Add(entity.Handle.GetValue());
                }
            }

            TrackPendingBlocks();
        });

    // A round restart's, once the map is up.
    public void OnEntitySpawned(IBaseEntity entity)
    {
        if (!_blocksActive || entity.Classname is not ("func_door" or "func_button"))
        {
            return;
        }

        _pendingBlocks.Add(entity.Handle.GetValue());

        if (!_blocksQueued)
        {
            _blocksQueued = true;

            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                _blocksQueued = false;
                TrackPendingBlocks();
            });
        }
    }

    public void OnEntityDeleted(IBaseEntity entity)
    {
        var handle = entity.Handle.GetValue();

        _inspected.Remove(handle);

        if (!_bhopBlocks.Remove(handle))
        {
            return;
        }

        foreach (var starts in _blockStarts)
        {
            starts?.Remove(handle);
        }
    }

    public BhopBlockSnapshot? CaptureBhopBlocks(PlayerSlot slot)
    {
        if (_bhopBlocks.Count == 0 || _globalVars is not { } globals)
        {
            return null;
        }

        var time   = globals.CurTime;
        var moving = new List<(uint Block, float Elapsed)>();

        if (_blockStarts[slot] is { } starts)
        {
            foreach (var (handle, start) in starts)
            {
                if (_bhopBlocks.TryGetValue(handle, out var block) && !block.AtRest(time - start))
                {
                    moving.Add((handle, time - start));
                }
            }
        }

        return new BhopBlockSnapshot(_lastGround[slot], moving.ToArray());
    }

    public void RestoreBhopBlocks(PlayerSlot slot, BhopBlockSnapshot? snapshot)
    {
        ClearBhopBlocks(slot);

        if (snapshot is null || _globalVars is not { } globals)
        {
            return;
        }

        var time   = globals.CurTime;
        var starts = _blockStarts[slot] ??= new Dictionary<uint, float>();

        foreach (var (handle, elapsed) in snapshot.Moving)
        {
            // Gone when the round restarted since: the map made its platforms anew, at rest.
            if (_bhopBlocks.ContainsKey(handle))
            {
                starts[handle] = time - elapsed;
            }
        }

        _lastGround[slot] = snapshot.Ground;
    }

    public void ClearBhopBlocks(PlayerSlot slot)
    {
        _blockStarts[slot]?.Clear();
        _lastGround[slot] = uint.MaxValue;
    }

    private static void ResetBhopBlocks()
    {
        _bhopBlocks.Clear();
        _pendingBlocks.Clear();
        _inspected.Clear();
        _blocksActive = false;
        _blocksQueued = false;

        foreach (var starts in _blockStarts)
        {
            starts?.Clear();
        }

        Array.Fill(_lastGround, uint.MaxValue);
    }

    private void TrackPendingBlocks()
    {
        if (_pendingBlocks.Count == 0 || !_mpbhopsEnabled || !_canTriggerJump || _mapInfo.GetCurrentGameMode() != EGameMode.Bhop)
        {
            _pendingBlocks.Clear();

            return;
        }

        Span<int> verdicts = stackalloc int[Enum.GetValues<BlockVerdict>().Length];

        foreach (var handle in _pendingBlocks)
        {
            if (_entityManager.FindEntityByHandle(new CEntityHandle<IBaseEntity>(handle)) is not { IsValidEntity: true } block)
            {
                continue;
            }

            // A spawn event and the activation both queue the same block.
            if (!_inspected.Add(handle))
            {
                continue;
            }

            verdicts[(int) TrackBhopBlock(block)]++;
        }

        _pendingBlocks.Clear();

        _logger.LogInformation("mpbhops: {Dropping} dropping and {Rising} rising bhop platforms kept still; skipped {NotTouched} not moved by touch, "
                               + "{NoTeleport} dropping into no trigger_teleport, {Still} not moving, {NoReturn} rising for good",
                               verdicts[(int) BlockVerdict.Dropping],
                               verdicts[(int) BlockVerdict.Rising],
                               verdicts[(int) BlockVerdict.NotTouched],
                               verdicts[(int) BlockVerdict.NoTeleport],
                               verdicts[(int) BlockVerdict.Still],
                               verdicts[(int) BlockVerdict.NoReturn]);
    }

    private static BlockVerdict TrackBhopBlock(IBaseEntity entity)
    {
        var button = entity.Classname == "func_button";
        var touch  = button ? SF_BUTTON_TOUCH_ACTIVATES : SF_DOOR_PTOUCH;
        var flags  = entity.SpawnFlags;

        if ((flags & touch) == 0)
        {
            return BlockVerdict.NotTouched;
        }

        var start = entity.GetNetVar<Vector>("m_vecPosition1");
        var end   = entity.GetNetVar<Vector>("m_vecPosition2");
        var speed = entity.GetNetVar<float>("m_flSpeed");
        var wait  = entity.GetNetVar<float>("m_flWait");

        if (start.Z == end.Z || speed <= 0.0f)
        {
            return BlockVerdict.Still;
        }

        var teleport = 0u;
        var depth    = 0.0f;

        if (start.Z > end.Z)
        {
            if (entity.GetCollisionProperty() is not { } collision
                || (teleport = FindTeleportBetween(start, end, collision.Mins, collision.Maxs, out depth)) == 0)
            {
                return BlockVerdict.NoTeleport;
            }
        }
        // Rising, and back down after a while: a booster.
        else if (wait <= 0.0f)
        {
            return BlockVerdict.NoReturn;
        }

        entity.SpawnFlags = button ? (flags & ~touch) | SF_BUTTON_DONTMOVE : (flags & ~touch) | SF_DOOR_SILENT;
        entity.AcceptInput("Lock");

        _bhopBlocks[entity.Handle.GetValue()]
            = new (teleport, depth, MathF.Abs(start.Z - end.Z), speed, (end - start).Length() / speed, wait);

        return teleport != 0 ? BlockVerdict.Dropping : BlockVerdict.Rising;
    }

    // The trigger_teleport the block sinks a player standing on it into, and how far it sinks before they touch it: the
    // highest its fall reaches. A map leaves one big trigger over the level's fall area instead of one under each block
    // (bhop_aztec: x -16..416, y -16..496, z 0..59 for the whole first level), and a trace from the block starts inside
    // its own brush, which reports no trigger at all: the swept boxes are compared instead.
    private static uint FindTeleportBetween(Vector start, Vector end, Vector mins, Vector maxs, out float depth)
    {
        var low = new Vector(MathF.Min(start.X, end.X) + mins.X,
                             MathF.Min(start.Y, end.Y) + mins.Y,
                             MathF.Min(start.Z, end.Z) + mins.Z);

        var high = new Vector(MathF.Max(start.X, end.X) + maxs.X,
                              MathF.Max(start.Y, end.Y) + maxs.Y,
                              MathF.Max(start.Z, end.Z) + maxs.Z);

        // Where the player stands on it, and how far down it takes them.
        var top  = start.Z + maxs.Z;
        var drop = start.Z - end.Z;

        var found = 0u;
        depth = float.MaxValue;

        IBaseEntity? entity = null;

        while ((entity = _entityManager.FindEntityByClassname(entity, "trigger_teleport")) is not null)
        {
            if (!entity.IsValidEntity || entity.GetCollisionProperty() is not { } collision)
            {
                continue;
            }

            var origin      = entity.GetAbsOrigin();
            var triggerLow  = origin + collision.Mins;
            var triggerHigh = origin + collision.Maxs;

            if (low.X > triggerHigh.X || high.X < triggerLow.X
                || low.Y > triggerHigh.Y || high.Y < triggerLow.Y
                || low.Z > triggerHigh.Z || high.Z < triggerLow.Z)
            {
                continue;
            }

            // The physics touches a trigger the player's hull comes within GameTouchMargin of. One reaching up to where
            // they stand is touched without the block sinking, and one below where it stops never is.
            var sink = top - triggerHigh.Z - GameTouchMargin;

            if (sink > 0.0f && sink <= drop && sink < depth)
            {
                found = entity.Handle.GetValue();
                depth = sink;
            }
        }

        return found;
    }

    // At the end of the player's tick, standing on what the movement left them on.
    private static void ApplyBhopBlocks(nint pawn, int slot)
    {
        var ground = *(uint*) (pawn + CBaseEntity_m_hGroundEntity_offset);

        _lastGround[slot] = ground;

        if (!_bhopBlocks.TryGetValue(ground, out var block)
            || _globalVars is not { } globals
            || !IsAlive(pawn)
            || GetMoveType(pawn) != MoveType.Walk)
        {
            return;
        }

        var elapsed = SetOff(slot, ground, block, globals.CurTime);

        // Down to its teleport: they rode it there, or landed on it while it was down.
        if (block.Teleport == 0 || block.Offset(elapsed) < block.Depth || _touchingCount[slot] >= MaxTriggerHits)
        {
            return;
        }

        var teleport = EntityFromHandle(block.Teleport);

        if (teleport == nint.Zero)
        {
            return;
        }

        var handle = GetRefHandle(pawn);

        if (!CanTouchUnqueried(teleport, pawn, handle) || !PassesTriggerFilters(teleport, pawn))
        {
            return;
        }

        // As the block dropping into it would.
        TouchUntilNextTick(slot, teleport, block.Teleport, pawn, handle);
    }

    // How long the block has been moving for the player, setting it off when it's at rest: they touch it.
    private static float SetOff(int slot, uint handle, in BhopBlock block, float time)
    {
        var starts = _blockStarts[slot] ??= new Dictionary<uint, float>();

        if (starts.TryGetValue(handle, out var start) && !block.AtRest(time - start))
        {
            return time - start;
        }

        starts[handle] = time;

        return 0.0f;
    }

    // How long the block has been moving for the player; false when it's at rest.
    private static bool IsMoving(int slot, uint handle, in BhopBlock block, float time, out float elapsed)
    {
        elapsed = 0.0f;

        if (_blockStarts[slot] is not { } starts || !starts.TryGetValue(handle, out var start))
        {
            return false;
        }

        elapsed = time - start;

        return !block.AtRest(elapsed);
    }

    // Jumping off a rising block while it rises: its speed, as it gives to whoever leaves it then.
    private void OnBhopBlockJump(IGameEvent e)
    {
        if (_bhopBlocks.Count == 0
            || _globalVars is not { } globals
            || e.GetPlayerController("userid") is not { IsValidEntity: true, IsFakeClient: false } controller)
        {
            return;
        }

        var slot   = (int) controller.PlayerSlot;
        var ground = _lastGround[slot];

        if (!_bhopBlocks.TryGetValue(ground, out var block)
            || block.Teleport != 0
            || !IsMoving(slot, ground, block, globals.CurTime, out var elapsed)
            || elapsed >= block.MoveTime
            || controller.GetPlayerPawn() is not { IsAlive: true } pawn)
        {
            return;
        }

        var baseVelocity = pawn.BaseVelocity;
        baseVelocity.Z    += block.Speed;
        pawn.BaseVelocity =  baseVelocity;
    }
}
