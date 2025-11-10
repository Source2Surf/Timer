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
using Sharp.Shared;
using Sharp.Shared.Hooks;

namespace SurfTimer.Managers;

internal class InlineHookWrapper
{
    private readonly IDetourHook _hook;

    public InlineHookWrapper(IDetourHook hook, nint address, nint funcPtr)
    {
        _hook = hook;

        _hook.Prepare(address, funcPtr);
    }

    public InlineHookWrapper(IDetourHook hook, string gamedata, nint funcPtr)
    {
        _hook = hook;

        _hook.Prepare(gamedata, funcPtr);
    }

    public bool Install()
    {
        var result = _hook.Install();

        if (!result)
        {
            _hook.Dispose();
        }

        return result;
    }

    public void Uninstall()
    {
        _hook.Uninstall();
        _hook.Dispose();
    }

    public nint Trampoline => _hook.Trampoline;
}

internal interface IInlineHookManager
{
    bool AddHook(ILibraryModule module, string pattern, nint funcPtr, out nint trampoline);

    bool AddHook(string gamedata, nint funcPtr, out nint trampoline);

    bool AddHook(nint address, nint funcPtr, out nint trampoline);
}

internal class InlineHookManager : IManager, IInlineHookManager
{
    private readonly InterfaceBridge         _bridge;
    private readonly List<InlineHookWrapper> _hooks = [];

    public InlineHookManager(InterfaceBridge bridge)
        => _bridge = bridge;

    public bool Init()
        => true;

    public void Shutdown()
    {
        foreach (var hook in _hooks)
        {
            hook.Uninstall();
        }
    }

    public bool AddHook(ILibraryModule module, string pattern, nint funcPtr, out nint trampoline)
    {
        var address = module.FindPattern(pattern);

        if (address == nint.Zero)
        {
            trampoline = 0;

            return false;
        }

        return AddHook(address, funcPtr, out trampoline);
    }

    public bool AddHook(string gamedata, IntPtr funcPtr, out nint trampoline)
    {
        var detour = _bridge.HookManager.CreateDetourHook();

        var hk = new InlineHookWrapper(detour, gamedata, funcPtr);

        var result = hk.Install();
        trampoline = 0;

        if (!result)
        {
            detour.Dispose();

            return false;
        }

        trampoline = hk.Trampoline;
        _hooks.Add(hk);

        return true;
    }

    public bool AddHook(nint address, nint funcPtr, out nint trampoline)
    {
        var detour = _bridge.HookManager.CreateDetourHook();

        var hk = new InlineHookWrapper(detour, address, funcPtr);

        var result = hk.Install();
        trampoline = 0;

        if (!result)
        {
            detour.Dispose();

            return false;
        }

        trampoline = hk.Trampoline;
        _hooks.Add(hk);

        return true;
    }
}
