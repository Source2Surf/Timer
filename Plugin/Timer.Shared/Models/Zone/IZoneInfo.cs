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

using Sharp.Shared.Types;

namespace Source2Surf.Timer.Shared.Models.Zone;

public interface IZoneInfo
{
    EZoneType ZoneType       { get; }
    int       Track          { get; }
    Vector    Corner1        { get; }
    Vector    Corner2        { get; }
    Vector?   TeleportOrigin { get; }
    Vector?   TeleportAngles { get; }
    Vector    Origin         { get; }
    bool      Prebuilt       { get; }
    int       Data           { get; }
}
