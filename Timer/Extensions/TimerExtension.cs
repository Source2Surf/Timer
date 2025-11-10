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
using Sharp.Shared;
using Sharp.Shared.Enums;

namespace SurfTimer.Extensions;

internal static class TimerExtension
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCall(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCall(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCallThisRound(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval, GameTimerFlags.StopOnRoundEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCallThisRound(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval, GameTimerFlags.StopOnRoundEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCallThisMap(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval, GameTimerFlags.StopOnMapEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid DelayCallThisMap(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval, GameTimerFlags.StopOnMapEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCall(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCall(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCallThisRound(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnRoundEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCallThisRound(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnRoundEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCallThisMap(this IModSharp sharp, double interval, Action call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Guid RepeatCallThisMap(this IModSharp sharp, double interval, Func<TimerAction> call)
        => sharp.PushTimer(call, interval, GameTimerFlags.Repeatable | GameTimerFlags.StopOnMapEnd);
}