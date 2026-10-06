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

using System.Numerics;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;

namespace Source2Surf.Timer.Modules;

internal interface IHideModule
{
}

/// <summary>
///     !hide: other players and replay bots stop being sent to the player, with their weapons and gunshots, except the
///     one they spectate. On or off is a player setting. !stopsound (SoundFilterModule's) blocks the gunshots alone.
/// </summary>
internal class HideModule : IModule, IHideModule, IPlayerManagerListener
{
    private readonly InterfaceBridge       _bridge;
    private readonly ITransmitManager      _transmit;
    private readonly ICommandManager       _commandManager;
    private readonly IPlayerManager        _playerManager;
    private readonly ILocalizationProvider _localization;
    private readonly IPlayerSettings       _settings;

    // Bit masks by player slot. _blocked holds, per receiver, the senders whose pawn is not sent to them.
    private          ulong   _joined;
    private          ulong   _hooked;
    private          ulong   _hiding;
    private          ulong   _noShots;
    private          ulong   _shotsBlocked;
    private readonly ulong[] _blocked = new ulong[PlayerSlot.MaxPlayerCount];

    public HideModule(InterfaceBridge       bridge,
                      ISharedSystem         shared,
                      ICommandManager       commandManager,
                      IPlayerManager        playerManager,
                      ILocalizationProvider localization,
                      IPlayerSettings       settings)
    {
        _bridge         = bridge;
        _transmit       = shared.GetTransmitManager();
        _commandManager = commandManager;
        _playerManager  = playerManager;
        _localization   = localization;
        _settings       = settings;
    }

    public bool Init()
    {
        _playerManager.RegisterListener(this);
        _settings.Changed += OnSettingsChanged;
        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);
        _bridge.HookManager.PlayerEquipWeapon.InstallForward(OnPlayerEquipWeapon);
        _bridge.HookManager.PlayerDropWeapon.InstallForward(OnPlayerDropWeapon);

        _commandManager.AddClientChatCommand("hide", OnCommandHide);

        return true;
    }

    public void Shutdown()
    {
        _playerManager.UnregisterListener(this);
        _settings.Changed -= OnSettingsChanged;
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
        _bridge.HookManager.PlayerEquipWeapon.RemoveForward(OnPlayerEquipWeapon);
        _bridge.HookManager.PlayerDropWeapon.RemoveForward(OnPlayerDropWeapon);

        // Nobody could undo a hide once this is unloaded.
        _hiding  = 0;
        _noShots = 0;
        ApplyAll();
    }

    // ------------------------------------------------------------------ players

    public void OnClientPutInServer(PlayerSlot slot)
    {
        // ModSharp drops the slot's controller hook on connect and resets what the slot receives on activate.
        Forget(slot);
        _joined |= Bit(slot);
        OnSettingsChanged(slot);
    }

    public void OnClientDisconnected(PlayerSlot slot)
        => Forget(slot);

    private void OnSettingsChanged(PlayerSlot slot)
    {
        if (_settings.HidesPlayers(slot))
        {
            _hiding |= Bit(slot);
        }
        else
        {
            _hiding &= ~Bit(slot);
        }

        if (_settings.HearsWeaponSounds(slot))
        {
            _noShots &= ~Bit(slot);
        }
        else
        {
            _noShots |= Bit(slot);
        }
    }

    private void Forget(PlayerSlot slot)
    {
        var bit = Bit(slot);

        _joined       &= ~bit;
        _hooked       &= ~bit;
        _hiding       &= ~bit;
        _noShots      &= ~bit;
        _shotsBlocked &= ~bit;

        for (var i = 0; i < _blocked.Length; i++)
        {
            _blocked[i] &= ~bit;
        }

        _blocked[slot] = 0;
    }

    private ECommandAction OnCommandHide(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var hiding = !_settings.HidesPlayers(slot);
        _settings.SetHidesPlayers(slot, hiding);
        OnSettingsChanged(slot);

        controller.PrintToChat(_localization.For(slot)[hiding ? ChatTexts.HideOn : ChatTexts.HideOff]);

        return ECommandAction.Handled;
    }

    // ------------------------------------------------------------------ transmit

    private void OnGameFramePost(bool simulating, bool firstTick, bool lastTick)
        => ApplyAll();

    private void ApplyAll()
    {
        var receivers = _hiding | _noShots | _shotsBlocked;

        for (var i = 0; i < _blocked.Length; i++)
        {
            if (_blocked[i] != 0)
            {
                receivers |= Bit(i);
            }
        }

        if (receivers == 0)
        {
            return;
        }

        if (_hiding != 0)
        {
            HookControllers();
        }

        for (var bits = receivers; bits != 0; bits &= bits - 1)
        {
            Apply((byte) BitOperations.TrailingZeroCount(bits));
        }
    }

    // Senders' controllers get hooked once someone hides; a hook on a controller covers its pawn.
    private void HookControllers()
    {
        for (var bits = _joined & ~_hooked; bits != 0; bits &= bits - 1)
        {
            var slot = (byte) BitOperations.TrailingZeroCount(bits);

            if (_bridge.EntityManager.FindPlayerControllerBySlot(slot) is { } controller
                && (_transmit.IsEntityHooked(controller) || _transmit.AddEntityHooks(controller, true)))
            {
                _hooked |= Bit(slot);
            }
        }
    }

    private void Apply(PlayerSlot receiver)
    {
        var hiding     = (_hiding & Bit(receiver)) != 0;
        var blockShots = hiding || (_noShots & Bit(receiver)) != 0;

        if (blockShots != ((_shotsBlocked & Bit(receiver)) != 0))
        {
            _transmit.SetTempEntState(BlockTempEntType.FireBullets, receiver, blockShots);
            _shotsBlocked ^= Bit(receiver);
        }

        var wanted = 0UL;

        if (hiding)
        {
            if (!_bridge.TryGetController(receiver, out var controller)
                || controller.ConnectedState != PlayerConnectedState.PlayerConnected)
            {
                return;
            }

            wanted = _hooked & ~Bit(receiver);

            if (_bridge.GetObservedSlot(controller) is { } observed)
            {
                wanted &= ~Bit(observed);
            }
        }

        var receiverIndex = new EntityIndex(receiver);

        for (var bits = wanted ^ _blocked[receiver]; bits != 0; bits &= bits - 1)
        {
            PlayerSlot sender = (byte) BitOperations.TrailingZeroCount(bits);
            var        block  = (wanted & Bit(sender)) != 0;

            if (_transmit.SetEntityState(new EntityIndex(sender), receiverIndex, !block, -1))
            {
                _blocked[receiver] ^= Bit(sender);
            }
            else
            {
                _hooked &= ~Bit(sender);
                _blocked[receiver] &= ~Bit(sender);
            }
        }
    }

    // A weapon follows its holder's controller hook, so it goes with the pawn.
    private void OnPlayerEquipWeapon(IPlayerEquipWeaponForwardParams @params)
    {
        var weapon = @params.Weapon;

        if (_transmit.IsEntityHooked(weapon) || _transmit.AddEntityHooks(weapon, true))
        {
            _transmit.SetEntityOwner(weapon.Index, @params.Controller.Index);
        }
    }

    private void OnPlayerDropWeapon(IPlayerDropWeaponForwardParams @params)
        => _transmit.SetEntityOwner(@params.Weapon.Index, EntityIndex.InvalidIndex);

    private static ulong Bit(int slot)
        => 1UL << slot;
}
