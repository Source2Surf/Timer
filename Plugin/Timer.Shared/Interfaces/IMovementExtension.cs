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

using Sharp.Shared.Units;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
///     A module that changes how players move, for the timer's stats to follow. Optional; without one each tick is
///     one acceleration. Main thread only.
/// </summary>
public interface IMovementExtension
{
    static readonly string Identity = typeof(IMovementExtension).FullName!;

    /// <summary>
    ///     The acceleration a ProcessMove step is part of, given its CMoveData. Steps split from one share it.
    /// </summary>
    long GetAccelerationStep(PlayerSlot slot, nint moveData);
}
