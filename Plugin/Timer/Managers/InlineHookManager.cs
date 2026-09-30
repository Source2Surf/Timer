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
using Sharp.Shared;
using Sharp.Shared.Hooks;

namespace Source2Surf.Timer.Managers;

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

internal class MidFuncHookWrapper
{
    private readonly IMidFuncHook _hook;

    public MidFuncHookWrapper(IMidFuncHook hook, nint address, nint funcPtr)
    {
        _hook = hook;

        _hook.Prepare(address, funcPtr);
    }

    public MidFuncHookWrapper(IMidFuncHook hook, string gamedata, nint funcPtr)
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
}

internal interface IInlineHookManager
{
    bool AddHook(ILibraryModule module, string pattern, nint funcPtr, out nint trampoline);

    bool AddHook(string gamedata, nint funcPtr, out nint trampoline);

    bool AddHook(nint address, nint funcPtr, out nint trampoline);

    bool AddMidFuncHook(ILibraryModule module, string pattern, nint funcPtr);

    bool AddMidFuncHook(string gamedata, nint funcPtr);

    bool AddMidFuncHook(nint address, nint funcPtr);
}

internal class InlineHookManager : IManager, IInlineHookManager
{
    private readonly InterfaceBridge          _bridge;
    private readonly List<InlineHookWrapper>  _hooks    = [];
    private readonly List<MidFuncHookWrapper> _midHooks = [];

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

        foreach (var hook in _midHooks)
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
            return false;
        }

        trampoline = hk.Trampoline;
        _hooks.Add(hk);

        return true;
    }

    public bool AddMidFuncHook(ILibraryModule module, string pattern, nint funcPtr)
    {
        var address = module.FindPattern(pattern);

        return AddMidFuncHook(address, funcPtr);
    }

    public bool AddMidFuncHook(string gamedata, IntPtr funcPtr)
    {
        if (funcPtr == 0)
        {
            return true;
        }

        var hk = new MidFuncHookWrapper(_bridge.HookManager.CreateMidFuncHook(), gamedata, funcPtr);

        var result = hk.Install();

        if (!result)
        {
            return false;
        }

        _midHooks.Add(hk);

        return true;
    }

    public bool AddMidFuncHook(nint address, nint funcPtr)
    {
        if (address == 0)
        {
            return true;
        }

        var hk = new MidFuncHookWrapper(_bridge.HookManager.CreateMidFuncHook(), address, funcPtr);

        var result = hk.Install();

        if (!result)
        {
            return false;
        }

        _midHooks.Add(hk);

        return true;
    }
}
