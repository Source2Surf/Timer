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

using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Managers.Country;

/// <summary>
///     The external <see cref="ICountryProvider" /> when a module registers one, else no country is known.
/// </summary>
internal sealed class CountryProviderProxy : ExternalModuleProxy<ICountryProvider>, ICountryProvider
{
    public CountryProviderProxy(ISharedSystem shared, ILogger<CountryProviderProxy> logger)
        : base(shared, new NoCountries(), logger)
    {
    }

    protected override string Identity     => ICountryProvider.Identity;
    protected override string ContractName => "ICountryProvider";

    protected override bool InitFallback()
        => true;

    protected override void ShutdownFallback()
    {
    }

    public Task<PlayerCountry?> GetCountryAsync(string address)
        => Current.GetCountryAsync(address);

    private sealed class NoCountries : ICountryProvider
    {
        public Task<PlayerCountry?> GetCountryAsync(string address)
            => Task.FromResult<PlayerCountry?>(null);
    }
}
