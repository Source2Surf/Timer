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
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
/// Tells where players connect from, for their profile. Register an implementation under <see cref="Identity" /> to
/// connect the Timer to a GeoIP database, a web API or anything else; without one, no country is shown.
/// Players can hide theirs with !country.
/// </summary>
public interface ICountryProvider
{
    static readonly string Identity = typeof(ICountryProvider).FullName!;

    /// <summary>
    /// The country of a player's <paramref name="address" /> (their IP, without the port), or null when unknown.
    /// Called on the game thread once per connection, when they're put in the server; the task may finish on any
    /// thread, and the Timer stops waiting for it after a few seconds.
    /// </summary>
    Task<PlayerCountry?> GetCountryAsync(string address);
}
