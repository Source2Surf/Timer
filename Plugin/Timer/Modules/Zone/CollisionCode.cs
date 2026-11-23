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
using Iced.Intel;

namespace Source2Surf.Timer.Modules.Zone;

// What CCollisionProperty's code gives away that no gamedata or schema has. read(address, length) gives the code at an
// address, or null.
internal static class CollisionCode
{
    // Bytes read of each function looked at.
    private const int Window = 0x400;

    // Bytes read of the function that creates a trigger: the box set up comes after spawning it.
    private const int CreateTriggerWindow = 0xC00;

    // Levels of calls and jumps followed from SetSolid: Linux's SetSolid jumps to its body, which makes the call.
    private const int Depth = 2;

    // Instructions read for the jump of a stub.
    private const int StubLength = 4;

    // The slot the schema puts after the capsule's radius, 8-aligned.
    public static int Expected(int capsuleRadiusOffset)
        => (capsuleRadiusOffset + sizeof(float) + 7) & ~7;

    // Where CCollisionProperty keeps the RnHull_t a SOLID_CYLINDER (8) trigger's physics are built from. It isn't a
    // schema field: it's the pointer right after m_flCapsuleRadius. SetSolid rebuilds it when the type is 8, storing what
    // the physics interface built: `call [rax+...]` then `mov [this+offset], rax`. Windows does it in a function that
    // compares [this+m_nSolidType] with 8 itself; Linux compares in SetSolid's body and calls a function that only builds
    // and stores. So the store counts where its function, or the one that called it, compares something with 8, and only
    // at the slot the schema puts after m_flCapsuleRadius. Null when no store matches it.
    public static int? ShapeSlot(ulong setSolid, int expected, Func<ulong, int, byte[]?> read)
    {
        var queue = new Queue<(ulong Address, int Depth, bool CallerComparesEight)>();
        var seen  = new HashSet<ulong>();
        queue.Enqueue((setSolid, 0, false));

        while (queue.TryDequeue(out var item))
        {
            if (!seen.Add(item.Address) || read(item.Address, Window) is not { } code)
            {
                continue;
            }

            var decoder       = Decoder.Create(64, new ByteArrayCodeReader(code), item.Address);
            var end           = item.Address + (ulong) code.Length;
            var comparesEight = false;
            var stores        = false;
            var targets       = new List<ulong>();
            var previous      = default(Instruction);

            while (decoder.IP < end)
            {
                decoder.Decode(out var instruction);

                if (instruction.IsInvalid)
                {
                    break;
                }

                // cmp <byte>, 8
                if (instruction.Mnemonic == Mnemonic.Cmp
                    && instruction.Op1Kind == OpKind.Immediate8
                    && instruction.Immediate8 == 8
                    && (instruction.Op0Kind == OpKind.Register ? instruction.Op0Register.GetSize() == 1 : instruction.MemorySize == MemorySize.UInt8))
                {
                    comparesEight = true;
                }

                // call [reg+x], then mov [reg+expected], rax
                if (previous.Mnemonic == Mnemonic.Call
                    && previous.Op0Kind == OpKind.Memory
                    && instruction.Mnemonic == Mnemonic.Mov
                    && instruction.Op0Kind == OpKind.Memory
                    && instruction.MemorySize == MemorySize.UInt64
                    && instruction.MemoryBase is not (Register.RSP or Register.RIP or Register.None)
                    && instruction.Op1Kind == OpKind.Register
                    && instruction.Op1Register == Register.RAX
                    && (long) instruction.MemoryDisplacement64 == expected)
                {
                    stores = true;
                }

                if (instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch
                    && instruction.Op0Kind == OpKind.NearBranch64)
                {
                    targets.Add(instruction.NearBranchTarget);
                }

                if (IsEnd(previous, instruction))
                {
                    break;
                }

                previous = instruction;
            }

            if (stores && (comparesEight || item.CallerComparesEight))
            {
                return expected;
            }

            if (item.Depth < Depth)
            {
                foreach (var target in targets)
                {
                    queue.Enqueue((target, item.Depth + 1, comparesEight));
                }
            }
        }

        return null;
    }

    // Where SetSolid's work is done. Linux's SetSolid is a stub (`cmp [rdi+m_nSolidType], sil; je; movzx; jmp body`) and
    // most callers, the box set up among them, check the type themselves and call the body: a call to either is a call
    // to SetSolid.
    public static ulong SetSolidBody(ulong setSolid, Func<ulong, int, byte[]?> read)
    {
        if (read(setSolid, Window) is not { } code)
        {
            return setSolid;
        }

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code), setSolid);

        for (var i = 0; i < StubLength; i++)
        {
            decoder.Decode(out var instruction);

            if (instruction.IsInvalid || instruction.FlowControl is FlowControl.Call or FlowControl.Return)
            {
                break;
            }

            if (instruction.FlowControl == FlowControl.UnconditionalBranch && instruction.Op0Kind == OpKind.NearBranch64)
            {
                return instruction.NearBranchTarget;
            }
        }

        return setSolid;
    }

    // The function that sets up a trigger's box once it's spawned: (CCollisionProperty*, MoveType_t, collision group,
    // Vector* mins, Vector* maxs, interacts as, interacts with). It drops the entity's physics, sets the bounds and the
    // collision attributes, calls SetSolid(SOLID_BBOX) and builds the physics again from the collision shape. The first
    // function the trigger's creation calls that calls SetSolid itself; null when none does.
    public static ulong? BoxInit(ulong createTrigger, ulong setSolid, ulong setSolidBody, Func<ulong, int, byte[]?> read)
    {
        if (read(createTrigger, CreateTriggerWindow) is not { } code)
        {
            return null;
        }

        var decoder = Decoder.Create(64, new ByteArrayCodeReader(code), createTrigger);
        var end     = createTrigger + (ulong) code.Length;
        var seen    = new HashSet<ulong>();

        while (decoder.IP < end)
        {
            decoder.Decode(out var instruction);

            if (instruction.IsInvalid)
            {
                continue;
            }

            if (instruction.FlowControl == FlowControl.Call
                && instruction.Op0Kind == OpKind.NearBranch64
                && seen.Add(instruction.NearBranchTarget)
                && CallsAny(instruction.NearBranchTarget, setSolid, setSolidBody, read))
            {
                return instruction.NearBranchTarget;
            }
        }

        return null;
    }

    // What the box set up does around SetSolid, for any solid type: first `call [rax+offset]`, the entity's vfunc that
    // drops its physics; then the call (Windows) or tail jump (Linux) to the function that builds them again from the
    // solid type, (CBodyComponent*, CBaseEntity*). That one is told by its second argument, the entity, loaded as
    // CCollisionProperty::m_pOuter (`mov rdx/rsi, [reg+8]`) since the last call: the calls between take an
    // immediate or a saved argument there, and blocks moved out of line come later. Null when either is missing.
    public static (int DropPhysics, ulong BuildPhysics)? PhysicsCalls(ulong boxInit, Func<ulong, int, byte[]?> read)
    {
        if (read(boxInit, Window) is not { } code)
        {
            return null;
        }

        var decoder  = Decoder.Create(64, new ByteArrayCodeReader(code), boxInit);
        var end      = boxInit + (ulong) code.Length;
        var drop     = (int?) null;
        var outerIn  = false;
        var previous = default(Instruction);

        while (decoder.IP < end)
        {
            decoder.Decode(out var instruction);

            if (instruction.IsInvalid)
            {
                return null;
            }

            if (drop is null
                && instruction.Mnemonic == Mnemonic.Call
                && instruction.Op0Kind == OpKind.Memory
                && instruction.MemoryBase is not (Register.RSP or Register.RIP or Register.None)
                && instruction.MemoryIndex == Register.None)
            {
                drop = (int) instruction.MemoryDisplacement64;
            }

            if (instruction.Op0Kind == OpKind.Register
                && instruction.Op0Register.GetFullRegister() is Register.RDX or Register.RSI
                && instruction.Mnemonic is not (Mnemonic.Cmp or Mnemonic.Test or Mnemonic.Push))
            {
                outerIn = instruction.Mnemonic == Mnemonic.Mov
                          && instruction.Op0Register.GetSize() == 8
                          && instruction.Op1Kind == OpKind.Memory
                          && instruction.MemoryBase is not (Register.RSP or Register.RBP or Register.RIP or Register.None)
                          && instruction.MemoryIndex == Register.None
                          && instruction.MemoryDisplacement64 == 8;
            }

            // Outside the function: its own branches land between its start and its end.
            if (instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch
                && instruction.Op0Kind == OpKind.NearBranch64
                && (instruction.NearBranchTarget < boxInit || instruction.NearBranchTarget >= end)
                && outerIn)
            {
                return drop is { } d ? (d, instruction.NearBranchTarget) : null;
            }

            if (instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
            {
                outerIn = false;
            }

            if (IsEnd(previous, instruction))
            {
                return null;
            }

            previous = instruction;
        }

        return null;
    }

    private static bool CallsAny(ulong function, ulong first, ulong second, Func<ulong, int, byte[]?> read)
    {
        if (read(function, Window) is not { } code)
        {
            return false;
        }

        var decoder  = Decoder.Create(64, new ByteArrayCodeReader(code), function);
        var end      = function + (ulong) code.Length;
        var previous = default(Instruction);

        while (decoder.IP < end)
        {
            decoder.Decode(out var instruction);

            if (instruction.IsInvalid)
            {
                return false;
            }

            if (instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch
                && instruction.Op0Kind == OpKind.NearBranch64
                && instruction.NearBranchTarget is var target
                && (target == first || target == second))
            {
                return true;
            }

            if (IsEnd(previous, instruction))
            {
                return false;
            }

            previous = instruction;
        }

        return false;
    }

    // A function's end: a return padded up to the next one.
    private static bool IsEnd(in Instruction previous, in Instruction instruction)
        => previous.FlowControl == FlowControl.Return && instruction.Mnemonic is Mnemonic.Int3 or Mnemonic.Nop;
}
