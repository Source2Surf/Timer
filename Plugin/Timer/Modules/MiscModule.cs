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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Managers;
using Source2Surf.Timer.Managers.Patch;
using Source2Surf.Timer.Native;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Modules;

namespace Source2Surf.Timer.Modules;

internal interface IMiscModule
{
}

internal unsafe partial class MiscModule : IModule, IMiscModule, IGameListener
{
    private readonly InterfaceBridge     _bridge;
    private readonly ICommandManager     _commandManager;
    private readonly IPatchManager       _patchManager;
    private readonly IReplayModule       _replayModule;
    private readonly IStyleModule        _styleModule;
    private readonly ITimerModule        _timerModule;
    private readonly ILogger<MiscModule> _logger;

    // ReSharper disable InconsistentNaming

    private readonly delegate* unmanaged<QuantizedEncoderReg_t*, bool, byte*, int, int, float, float, void>
        FindOrCreateQuantizedFloatEncoder;
    private short g_FixedVelocityEncoderIndex;

    private static int CBaseEntity_m_vecVelocity_offset;

    // ReSharper restore InconsistentNaming

    // cvars
    // ReSharper disable InconsistentNaming

    private readonly IConVar timer_god_mode;
    private readonly IConVar timer_remove_dropped_weapons;
    private readonly IConVar timer_remove_weapons_on_spawn;
    private readonly IConVar timer_desubtick_jump;

    // ReSharper restore InconsistentNaming

    public MiscModule(InterfaceBridge     bridge,
                      ICommandManager     commandManager,
                      IInlineHookManager  inlineHookManager,
                      IPatchManager       patchManager,
                      IReplayModule       replayModule,
                      IStyleModule        styleModule,
                      ITimerModule        timerModule,
                      ILogger<MiscModule> logger)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _patchManager   = patchManager;
        _replayModule   = replayModule;
        _styleModule    = styleModule;
        _timerModule    = timerModule;
        _logger         = logger;

        CBaseEntity_m_vecVelocity_offset = bridge.SchemaManager.GetNetVarOffset("CBaseEntity", "m_vecVelocity");

        timer_god_mode                = bridge.ConVarManager.CreateConVar("timer_god_mode", 1)!;
        timer_remove_dropped_weapons  = bridge.ConVarManager.CreateConVar("timer_remove_dropped_weapons", true)!;
        timer_remove_weapons_on_spawn = bridge.ConVarManager.CreateConVar("timer_remove_weapons_on_spawn", true)!;

        timer_desubtick_jump = bridge.ConVarManager.CreateConVar("timer_desubtick_jump",
                                                                 true,
                                                                 "Enable this convar to prevent players from gaining extra speed by abusing subtick with spamming jump")
            !;

        FindOrCreateQuantizedFloatEncoder
            = (delegate* unmanaged<QuantizedEncoderReg_t*, bool, byte*, int, int, float, float, void>)
            bridge.Modules.Tier0.GetFunctionByName("FindOrCreateQuantizedFloatEncoder");

        if (FindOrCreateQuantizedFloatEncoder == null)
        {
            _logger.LogWarning("Failed to find FindOrCreateQuantizedFloatEncoder from tier0");
        }
    }

    public bool Init()
    {
        AddCommands();

        _bridge.ModSharp.InstallGameListener(this);

        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);
        _bridge.HookManager.TerminateRound.InstallHookPre(OnTerminateRoundPre);
        _bridge.HookManager.PlayerDispatchTraceAttack.InstallHookPre(OnPlayerDispatchAttackPre);
        _bridge.HookManager.PlayerDropWeapon.InstallForward(OnPlayerDropWeapon);

        _bridge.HookManager.PlayerRunCommand.InstallHookPre(OnPlayerRunCommand);

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        PatchTheNavCheck();
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _bridge.HookManager.TerminateRound.RemoveHookPre(OnTerminateRoundPre);
        _bridge.HookManager.PlayerDispatchTraceAttack.RemoveHookPre(OnPlayerDispatchAttackPre);
        _bridge.HookManager.PlayerDropWeapon.RemoveForward(OnPlayerDropWeapon);
        _bridge.HookManager.PlayerRunCommand.RemoveHookPre(OnPlayerRunCommand);
    }

    public void OnGameActivate()
    {
        // CreateUnclampedDecoder();
    }

    public void OnServerActivate()
    {
    }

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        var pawn = @params.Pawn;

        // ApplyUnclampedEncoder(pawn);

        if (timer_remove_weapons_on_spawn.GetBool())
        {
            pawn.RemoveAllItems(true);
        }
    }

#region Hooks & Forwards

    private static HookReturnValue<EmptyHookReturn> OnTerminateRoundPre(ITerminateRoundHookParams        @params,
                                                                        HookReturnValue<EmptyHookReturn> ret)
    {
        if (@params.Reason == RoundEndReason.GameCommencing)
        {
            return new (EHookAction.SkipCallReturnOverride);
        }

        return new (EHookAction.Ignored);
    }

    private HookReturnValue<long> OnPlayerDispatchAttackPre(IPlayerDispatchTraceAttackHookParams @params,
                                                            HookReturnValue<long>                ret)
    {
        if (timer_god_mode.GetInt32() == 0)
        {
            return new (EHookAction.Ignored);
        }

        return new (EHookAction.SkipCallReturnOverride);
    }

    private void OnPlayerDropWeapon(IPlayerDropWeaponForwardParams @params)
    {
        if (timer_remove_dropped_weapons.GetBool())
        {
            @params.Weapon.Kill();
        }
    }

    private HookReturnValue<EmptyHookReturn> OnPlayerRunCommand(IPlayerRunCommandHookParams      @params,
                                                                HookReturnValue<EmptyHookReturn> ret)
    {
        if (!timer_desubtick_jump.GetBool())
            return new ();

        var client = @params.Client;
        var slot   = client.Slot;

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo
            || _styleModule.GetStyleSetting(timerInfo.Style) is
            {
                AutoBhop: false, // we don't remove jump button from subtick moves if autobhop is off, because that will make scroll-jumping stop working
            })
            return new ();

        var subtickMoveSize = @params.SubtickMoveSize;

        for (var i = 0; i < subtickMoveSize; i++)
        {
            var subtickMove = @params.GetSubtickMove(i);

            if (subtickMove == null)
                continue;

            if ((subtickMove->Buttons & UserCommandButtons.Jump) != 0)
                subtickMove->Buttons &= ~UserCommandButtons.Jump;
        }

        return new HookReturnValue<EmptyHookReturn>();
    }

#endregion

#region FloatEncoder

    private void CreateUnclampedDecoder()
    {
        var reg = stackalloc QuantizedEncoderReg_t[1];

        fixed (byte* name = "better_m_vecX"u8)
        {
            FindOrCreateQuantizedFloatEncoder(reg,
                                              true,
                                              name,
                                              32,
                                              4,
                                              -ushort.MaxValue,
                                              ushort.MaxValue);
        }

        g_FixedVelocityEncoderIndex = reg->m_unEncoder;
    }

    private void ApplyUnclampedEncoder(IPlayerPawn pawn)
    {
        if (g_FixedVelocityEncoderIndex == 0)
        {
            CreateUnclampedDecoder();
        }

        var pawnNetworkedVelocity = (CNetworkVelocityVector*) (pawn.GetAbsPtr() + CBaseEntity_m_vecVelocity_offset);

        pawnNetworkedVelocity->m_vecX.m_nEncoder = g_FixedVelocityEncoderIndex;
        pawnNetworkedVelocity->m_vecY.m_nEncoder = g_FixedVelocityEncoderIndex;
        pawnNetworkedVelocity->m_vecZ.m_nEncoder = g_FixedVelocityEncoderIndex;
    }

#endregion
}
