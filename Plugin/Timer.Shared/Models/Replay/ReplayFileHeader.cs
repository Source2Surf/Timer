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

using System.Collections.Generic;

namespace Source2Surf.Timer.Shared.Models.Replay;

public record ReplayFileHeader
{
    public int Version { get; init; } = 1;

    public ulong SteamId     { get; init; }
    public int   TotalFrames { get; init; }
    public int   PreFrame    { get; init; }
    public int   PostFrame   { get; init; }
    public float Time        { get; init; }

    public string PlayerName { get; init; } = string.Empty;

    public List<int>? StageTicks { get; init; }
}
