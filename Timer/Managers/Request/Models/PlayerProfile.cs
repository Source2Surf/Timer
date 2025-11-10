/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
using Sharp.Shared.Units;

namespace SurfTimer.Managers.Request.Models;

internal class PlayerProfile
{
    public Guid Id { get; set; }

    public SteamID SteamId { get; init; }
    public string  Name    { get; private set; }

    public uint Points { get; set; }

    public DateTime JoinDate     { get; set; }
    public DateTime LastSeenDate { get; set; }

    public void UpdateName(string name)
        => Name = name;
}
