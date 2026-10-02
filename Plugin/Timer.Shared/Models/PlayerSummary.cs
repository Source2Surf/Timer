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

namespace Source2Surf.Timer.Shared.Models;

/// <summary>
/// A player's results across every map, for their profile: per style, what they've completed and the records
/// they hold, out of what the server has; and their time played.
/// </summary>
public sealed class PlayerSummary
{
    /// <summary>
    /// The maps the server knows, and the bonuses on them.
    /// </summary>
    public int TotalMaps    { get; init; }
    public int TotalBonuses { get; init; }

    /// <summary>
    /// Seconds played across every map, all styles.
    /// </summary>
    public float PlayTime { get; init; }

    /// <summary>
    /// One entry per style the player has finished anything on.
    /// </summary>
    public IReadOnlyList<PlayerStyleSummary> Styles { get; init; } = [];
}

/// <summary>
/// One style of a <see cref="PlayerSummary" />: maps (main track) and bonuses completed, and the server records
/// held on maps, bonuses and stages.
/// </summary>
public sealed record PlayerStyleSummary(int Style,
                                        int MapsCompleted,
                                        int BonusesCompleted,
                                        int MapRecords,
                                        int BonusRecords,
                                        int StageRecords);
