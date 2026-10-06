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
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

internal interface ICountryModule
{
    /// <summary>
    ///     Where the player connects from, when the <see cref="ICountryProvider" /> knows and they show it.
    /// </summary>
    PlayerCountry? GetShownCountry(PlayerSlot slot);
}

/// <summary>
///     The player's country for their profile, when the <see cref="ICountryProvider" /> knows it and they haven't
///     hidden it (!country). Hidden until their settings load, and when the backend couldn't load them.
/// </summary>
internal class CountryModule : IModule, ICountryModule, IClientListener
{
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly InterfaceBridge        _bridge;
    private readonly ICountryProvider       _provider;
    private readonly ICommandManager        _commandManager;
    private readonly ILocalizationProvider  _localization;
    private readonly ILogger<CountryModule> _logger;

    private IPlayerSettings _settings = null!;

    // By slot: this connection's lookup, and whether their settings are known.
    private readonly Lookup?[] _lookups       = new Lookup?[PlayerSlot.MaxPlayerCount];
    private readonly bool[]    _settingsKnown = new bool[PlayerSlot.MaxPlayerCount];

    private sealed class Lookup
    {
        public PlayerCountry? Country;
    }

    public CountryModule(InterfaceBridge        bridge,
                         ICountryProvider       provider,
                         ICommandManager        commandManager,
                         ILocalizationProvider  localization,
                         ILogger<CountryModule> logger)
    {
        _bridge         = bridge;
        _provider       = provider;
        _commandManager = commandManager;
        _localization   = localization;
        _logger         = logger;
    }

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);
        _commandManager.AddClientChatCommand("country", OnCommandCountry);

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _settings        =  provider.GetRequiredService<IPlayerSettings>();
        _settings.Loaded += OnSettingsLoaded;
    }

    public void Shutdown()
    {
        _bridge.ClientManager.RemoveClientListener(this);
        _settings.Loaded -= OnSettingsLoaded;
    }

    public PlayerCountry? GetShownCountry(PlayerSlot slot)
        => _settingsKnown[slot] && _settings.ShowsCountry(slot) ? _lookups[slot]?.Country : null;

    // Once a connection, though a map change puts players in the server again.
    public void OnClientPutInServer(IGameClient client)
    {
        var slot = client.Slot;

        if (client.IsFakeClient || _lookups[slot] is not null || client.GetAddress(false) is not { Length: > 0 } address)
        {
            return;
        }

        var lookup = new Lookup();
        _lookups[slot] = lookup;
        _ = LookUpAsync(slot, lookup, address);
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        _lookups[client.Slot]       = null;
        _settingsKnown[client.Slot] = false;
    }

    private async Task LookUpAsync(PlayerSlot slot, Lookup lookup, string address)
    {
        PlayerCountry? country = null;

        try
        {
            country = await _provider.GetCountryAsync(address).WaitAsync(LookupTimeout).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Country lookup failed for slot {Slot}", (int) slot);
        }

        try
        {
            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_lookups[slot] == lookup)
                {
                    lookup.Country = country;
                }
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Country lookup finished after shutdown");
        }
    }

    private void OnSettingsLoaded(PlayerSlot slot, bool known)
        => _settingsKnown[slot] = known;

    private ECommandAction OnCommandCountry(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var shows = !_settings.ShowsCountry(slot);
        _settings.SetShowsCountry(slot, shows);

        controller.PrintToChat(_localization.For(slot)[shows ? ChatTexts.CountryShown : ChatTexts.CountryHidden]);

        return ECommandAction.Handled;
    }
}
