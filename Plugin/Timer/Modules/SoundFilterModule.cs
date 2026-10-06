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
using System.Numerics;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;

namespace Source2Surf.Timer.Modules;

internal interface ISoundFilterModule
{
}

/// <summary>
///     !footsteps and !stopsound: other players' (and replay bots') footsteps and weapon sounds stop being sent to the
///     player; their own still play. Both are player settings. The game sends a step through its sound emitter to
///     everyone but the stepper, so the player leaves that send; gunshots are FireBullets temp entities, which
///     HideModule blocks, and CS_UM_WeaponSound messages.
/// </summary>
internal class SoundFilterModule : IModule, ISoundFilterModule, IPlayerManagerListener
{
    private readonly InterfaceBridge       _bridge;
    private readonly ICommandManager       _commandManager;
    private readonly IPlayerManager        _playerManager;
    private readonly ILocalizationProvider _localization;
    private readonly IPlayerSettings       _settings;

    // By player slot: who doesn't hear others' footsteps, and who doesn't hear their weapons.
    private ulong _noFootsteps;
    private ulong _noWeapons;

    public SoundFilterModule(InterfaceBridge       bridge,
                             ICommandManager       commandManager,
                             IPlayerManager        playerManager,
                             ILocalizationProvider localization,
                             IPlayerSettings       settings)
    {
        _bridge         = bridge;
        _commandManager = commandManager;
        _playerManager  = playerManager;
        _localization   = localization;
        _settings       = settings;
    }

    public bool Init()
    {
        _playerManager.RegisterListener(this);
        _settings.Changed += OnSettingsChanged;
        _bridge.HookManager.EmitSound.InstallHookPre(OnEmitSoundPre);
        _bridge.HookManager.SoundEvent.InstallHookPre(OnSoundEventPre);
        _bridge.HookManager.PostEventAbstract.InstallHookPre(OnPostEventPre);

        _commandManager.AddClientChatCommand("footsteps", OnCommandFootsteps);
        _commandManager.AddClientChatCommand("stopsound", OnCommandStopSound);

        return true;
    }

    public void Shutdown()
    {
        _playerManager.UnregisterListener(this);
        _settings.Changed -= OnSettingsChanged;
        _bridge.HookManager.EmitSound.RemoveHookPre(OnEmitSoundPre);
        _bridge.HookManager.SoundEvent.RemoveHookPre(OnSoundEventPre);
        _bridge.HookManager.PostEventAbstract.RemoveHookPre(OnPostEventPre);
    }

    public void OnClientPutInServer(PlayerSlot slot)
        => OnSettingsChanged(slot);

    public void OnClientDisconnected(PlayerSlot slot)
    {
        _noFootsteps &= ~Bit(slot);
        _noWeapons   &= ~Bit(slot);
    }

    private void OnSettingsChanged(PlayerSlot slot)
    {
        _noFootsteps = Set(_noFootsteps, slot, !_settings.HearsFootsteps(slot));
        _noWeapons   = Set(_noWeapons, slot, !_settings.HearsWeaponSounds(slot));
    }

    private ECommandAction OnCommandFootsteps(PlayerSlot slot, StringCommand command)
        => Toggle(slot, _settings.HearsFootsteps, _settings.SetHearsFootsteps, ChatTexts.FootstepsOn, ChatTexts.FootstepsOff);

    private ECommandAction OnCommandStopSound(PlayerSlot slot, StringCommand command)
        => Toggle(slot, _settings.HearsWeaponSounds, _settings.SetHearsWeaponSounds, ChatTexts.WeaponSoundsOn, ChatTexts.WeaponSoundsOff);

    private ECommandAction Toggle(PlayerSlot slot, Func<PlayerSlot, bool> get, Action<PlayerSlot, bool> set, ChatText on, ChatText off)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var hears = !get(slot);
        set(slot, hears);
        OnSettingsChanged(slot);

        controller.PrintToChat(_localization.For(slot)[hears ? on : off]);

        return ECommandAction.Handled;
    }

    // ------------------------------------------------------------------ sounds

    private HookReturnValue<SoundOpEventGuid> OnEmitSoundPre(IEmitSoundHookParams @params, HookReturnValue<SoundOpEventGuid> previous)
        => Filter(@params.EntityIndex, @params.SoundName, @params.HasReceiver, @params.RemoveReceiver);

    private HookReturnValue<SoundOpEventGuid> OnSoundEventPre(ISoundEventHookParams @params, HookReturnValue<SoundOpEventGuid> previous)
        => Filter(@params.EntityIndex, @params.SoundName, @params.HasReceiver, @params.RemoveReceiver);

    private HookReturnValue<SoundOpEventGuid> Filter(EntityIndex entity,
                                                    string sound,
                                                    Func<PlayerSlot, bool> hasReceiver,
                                                    Action<PlayerSlot> removeReceiver)
    {
        var blocked = (_noFootsteps | _noWeapons) == 0 ? SoundKind.Other : Classify(sound);
        var muting  = blocked switch
        {
            SoundKind.Footstep => _noFootsteps,
            SoundKind.Weapon   => _noWeapons,
            _                  => 0UL,
        };

        if (muting == 0 || SourceSlot(entity) is not { } source)
        {
            return new (EHookAction.Ignored);
        }

        var removed = false;

        for (var bits = muting & ~Bit(source); bits != 0; bits &= bits - 1)
        {
            PlayerSlot slot = (byte) BitOperations.TrailingZeroCount(bits);

            if (hasReceiver(slot))
            {
                removeReceiver(slot);
                removed = true;
            }
        }

        return new (removed ? EHookAction.ChangeParamReturnDefault : EHookAction.Ignored);
    }

    private HookReturnValue<NetworkReceiver> OnPostEventPre(IPostEventAbstractHookParams @params, HookReturnValue<NetworkReceiver> previous)
    {
        if (_noWeapons == 0
            || @params.MsgId != ProtobufNetMessageType.CS_UM_WeaponSound
            || @params.Data.ReadInt32("entidx") is not { } index
            || SourceSlot(new EntityIndex(index)) is not { } source)
        {
            return new (EHookAction.Ignored);
        }

        var receivers = @params.Receivers;
        var kept      = receivers;

        for (var bits = _noWeapons & ~Bit(source); bits != 0; bits &= bits - 1)
        {
            kept = kept.Remove((byte) BitOperations.TrailingZeroCount(bits));
        }

        return kept.Count() == receivers.Count()
            ? new (EHookAction.Ignored)
            : new (EHookAction.ChangeParamReturnDefault, kept);
    }

    // The player whose pawn, or whose weapon, makes the sound; world sounds aren't touched.
    private PlayerSlot? SourceSlot(EntityIndex index)
    {
        if (_bridge.EntityManager.FindEntityByIndex<IBaseEntity>(index) is not { IsValidEntity: true } entity)
        {
            return null;
        }

        var pawn = entity.IsPlayerPawn ? entity.AsPlayerPawn() : entity.OwnerEntity?.AsPlayerPawn();

        return pawn?.GetControllerAuto()?.PlayerSlot;
    }

    internal enum SoundKind
    {
        Other,
        Footstep,
        Weapon,
    }

    // By CS2's sound event names (soundevents/game_sounds_footsteps and game_sounds_weapons).
    internal static SoundKind Classify(string sound)
    {
        if (sound.Contains(".Step", StringComparison.OrdinalIgnoreCase)
            || sound.StartsWith("Land", StringComparison.OrdinalIgnoreCase)
            || sound.Contains(".Land", StringComparison.OrdinalIgnoreCase)
            || sound.StartsWith("Gear.", StringComparison.OrdinalIgnoreCase)
            || sound.EndsWith(".Splash", StringComparison.OrdinalIgnoreCase)
            || sound.Equals("Player.Wade", StringComparison.OrdinalIgnoreCase)
            || sound.Equals("Base.Footstep", StringComparison.OrdinalIgnoreCase))
        {
            return SoundKind.Footstep;
        }

        if (sound.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase)
            || sound.Contains("Knife", StringComparison.OrdinalIgnoreCase)
            || sound.Contains("Grenade", StringComparison.OrdinalIgnoreCase)
            || sound.StartsWith("Molotov", StringComparison.OrdinalIgnoreCase)
            || sound.StartsWith("Flashbang", StringComparison.OrdinalIgnoreCase)
            || sound.StartsWith("Decoy", StringComparison.OrdinalIgnoreCase))
        {
            return SoundKind.Weapon;
        }

        return SoundKind.Other;
    }

    private static ulong Set(ulong mask, PlayerSlot slot, bool on)
        => on ? mask | Bit(slot) : mask & ~Bit(slot);

    private static ulong Bit(int slot)
        => 1UL << slot;
}
