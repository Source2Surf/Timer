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

internal static class VoteBuilder
{
    /// <summary>
    ///     The nominated maps first, then random ones from the candidates up to <paramref name="slots" />, then
    ///     extending this map when <paramref name="extendMinutes" /> is set.
    /// </summary>
    public static List<MapVoteOption> Build(IEnumerable<PoolMap>  nominated,
                                            IEnumerable<PoolMap>  candidates,
                                            int                   slots,
                                            int                   extendMinutes,
                                            Random                random)
    {
        var options = new List<MapVoteOption>();
        var taken   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var map in nominated)
        {
            if (options.Count >= slots)
            {
                break;
            }

            if (taken.Add(map.Name))
            {
                options.Add(new MapVoteOption(map.Name, map.Tier));
            }
        }

        var rest = candidates.Where(x => !taken.Contains(x.Name)).ToArray();
        random.Shuffle(rest);

        options.AddRange(rest.Take(slots - options.Count).Select(x => new MapVoteOption(x.Name, x.Tier)));

        if (extendMinutes > 0)
        {
            options.Add(new MapVoteOption(string.Empty, 0, extendMinutes));
        }

        return options;
    }
}
