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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Source2Surf.Timer;

/// <summary>
///     JIT-compiles the plugin off the game thread after load. ModSharp's collectible, in-memory load context ignores
///     ReadyToRun code, so otherwise the first finish, PB and replay save compile on the game thread and hitch.
/// </summary>
internal static class JitWarmup
{
    private const BindingFlags AllDeclared = BindingFlags.Instance
                                             | BindingFlags.Static
                                             | BindingFlags.Public
                                             | BindingFlags.NonPublic
                                             | BindingFlags.DeclaredOnly;

    internal readonly record struct Result(int                   Prepared,
                                           IReadOnlyList<string> Failed,
                                           TimeSpan              Elapsed,
                                           TimeSpan              JitTime,
                                           bool                  Cancelled);

    // Below normal priority, so the game thread wins any contention for a core.
    public static void Start(IReadOnlyList<Assembly> assemblies, ILogger logger, CancellationToken token)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var result = Run(assemblies, token);

                if (!result.Cancelled)
                {
                    logger.LogInformation("JIT warm-up compiled {Prepared} methods in {Elapsed:F0} ms, {Jit:F0} ms of it JIT",
                                          result.Prepared,
                                          result.Elapsed.TotalMilliseconds,
                                          result.JitTime.TotalMilliseconds);
                }

                if (result.Failed.Count > 0)
                {
                    logger.LogDebug("JIT warm-up left {Count} methods to compile on first call: {Methods}",
                                    result.Failed.Count,
                                    result.Failed);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "JIT warm-up failed");
            }
        })
        {
            IsBackground = true,
            Priority     = ThreadPriority.BelowNormal,
            Name         = "Timer JIT warm-up",
        };

        thread.Start();
    }

    internal static Result Run(IReadOnlyList<Assembly> assemblies, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        var jitBefore = JitInfo.GetCompilationTime(currentThread: true);
        var prepared  = 0;
        var failed    = new List<string>();

        foreach (var method in assemblies.SelectMany(GetTypes).SelectMany(GetCompilableMethods))
        {
            if (token.IsCancellationRequested)
            {
                return new (prepared, failed, stopwatch.Elapsed, JitTime(), true);
            }

            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
            catch (Exception e)
            {
                // The method still compiles normally on its first call.
                failed.Add($"{method.DeclaringType?.FullName}.{method.Name} ({e.GetType().Name}: {e.Message})");
            }
        }

        return new (prepared, failed, stopwatch.Elapsed, JitTime(), false);

        TimeSpan JitTime() => JitInfo.GetCompilationTime(currentThread: true) - jitBefore;
    }

    private static IEnumerable<Type> GetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    // Open generics need a concrete instantiation to compile, and methods without IL (abstract,
    // extern, runtime-implemented) have nothing to compile.
    private static IEnumerable<MethodBase> GetCompilableMethods(Type type)
    {
        if (type.ContainsGenericParameters)
        {
            return [];
        }

        return type.GetMethods(AllDeclared)
                   .Cast<MethodBase>()
                   .Concat(type.GetConstructors(AllDeclared))
                   .Where(m => !m.IsAbstract
                               && !m.ContainsGenericParameters
                               && (m.Attributes & MethodAttributes.PinvokeImpl) == 0
                               && (m.MethodImplementationFlags & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.IL
                               && (m.MethodImplementationFlags & MethodImplAttributes.InternalCall) == 0);
    }
}
