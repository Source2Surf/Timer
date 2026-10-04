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

using System.Runtime.CompilerServices;
using Iced.Intel;
using Sharp.Shared.Hooks;

namespace Source2Surf.Timer.Extensions;

internal static class MidHookContextExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe ref nint GetGpr(this ref MidHookContext ctx, Register register)
    {
        fixed (MidHookContext* p = &ctx)
        {
            switch (register)
            {
                case Register.RAX:
                    return ref p->rax;
                case Register.RBX:
                    return ref p->rbx;
                case Register.RCX:
                    return ref p->rcx;
                case Register.RDX:
                    return ref p->rdx;
                case Register.RSI:
                    return ref p->rsi;
                case Register.RDI:
                    return ref p->rdi;
                case Register.RBP:
                    return ref p->rbp;
                case Register.RSP:
                    return ref p->rsp;
                case Register.R8:
                    return ref p->r8;
                case Register.R9:
                    return ref p->r9;
                case Register.R10:
                    return ref p->r10;
                case Register.R11:
                    return ref p->r11;
                case Register.R12:
                    return ref p->r12;
                case Register.R13:
                    return ref p->r13;
                case Register.R14:
                    return ref p->r14;
                case Register.R15:
                    return ref p->r15;
                default:
                    return ref Unsafe.NullRef<nint>();
            }
        }
    }
}
