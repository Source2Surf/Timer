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
using System.Linq;
using Source2Surf.Timer.Shared.Models;

namespace Timer.MapChooser.Logic;

/// <param name="MapId">The map's id in the timer's database, or 0 when it has never been played here.</param>
/// <param name="Tier">0 when unknown.</param>
/// <param name="Hosted">One of the server's workshop maps, changed to by name.</param>
internal sealed record PoolMap(string Name, ulong WorkshopId, ulong MapId, byte Tier, bool Hosted);

internal static class MapPool
{
    /// <summary>
    ///     The maps to choose from: the server's workshop maps, then the extra local ones, minus the excluded, in name
    ///     order. A workshop map takes its tier from its workshop item's row, or else from the row of the same name.
    /// </summary>
    public static List<PoolMap> Build(IEnumerable<(ulong Id, string Name)> hosted,
                                      IEnumerable<string>                  extra,
                                      IReadOnlyList<MapProfile>            profiles,
                                      IEnumerable<string>                  exclude)
    {
        var byWorkshop = new Dictionary<ulong, MapProfile>();
        var byName     = new Dictionary<string, MapProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var profile in profiles)
        {
            if (profile.WorkshopId != 0)
            {
                byWorkshop.TryAdd(profile.WorkshopId, profile);
            }

            byName.TryAdd(profile.MapName, profile);
        }

        var excluded = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var maps     = new Dictionary<string, PoolMap>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name) in hosted)
        {
            if (string.IsNullOrWhiteSpace(name) || excluded.Contains(name))
            {
                continue;
            }

            var profile = byWorkshop.GetValueOrDefault(id) ?? byName.GetValueOrDefault(name);
            maps.TryAdd(name, new PoolMap(name, id, profile?.MapId ?? 0, profile?.Tier[0] ?? 0, true));
        }

        foreach (var name in extra)
        {
            if (string.IsNullOrWhiteSpace(name) || excluded.Contains(name))
            {
                continue;
            }

            var profile = byName.GetValueOrDefault(name);
            maps.TryAdd(name, new PoolMap(name, 0, profile?.MapId ?? 0, profile?.Tier[0] ?? 0, false));
        }

        return maps.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
