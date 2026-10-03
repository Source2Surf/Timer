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

namespace Source2Surf.Timer.Modules.MapInfo;

internal static class WorkshopMaps
{
    /// <summary>
    ///     The workshop item of the running map, or 0 when it isn't from the workshop. The addon list alone
    ///     can't tell: it also carries addons that aren't maps, such as the HUD's.
    /// </summary>
    public static ulong FindItemId(string                                    mapName,
                                   IEnumerable<(ulong PublishFileId, string Name)> workshopMaps,
                                   string?                                   addons)
    {
        var items = workshopMaps.Where(x => string.Equals(x.Name, mapName, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.PublishFileId)
                                .Distinct()
                                .ToList();

        if (items.Count <= 1)
        {
            return items.FirstOrDefault();
        }

        foreach (var addon in (addons ?? string.Empty).Split(',', StringSplitOptions.TrimEntries))
        {
            if (ulong.TryParse(addon, out var id) && items.Contains(id))
            {
                return id;
            }
        }

        return 0;
    }
}
