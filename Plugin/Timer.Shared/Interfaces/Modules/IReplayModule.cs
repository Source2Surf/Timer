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
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Shared.Interfaces.Modules;

/// <summary>
/// Public interface for the replay module, providing bot data access.
/// </summary>
public interface IReplayModule
{
    IReplayBotData? GetReplayBotData(PlayerSlot slot);
    IReplayBotData? GetReplayBotByIndex(int index);

    /// <summary>
    ///     Returns the cached replay for <c>(style, track, stage)</c>, or <c>null</c> when not loaded.
    /// </summary>
    ReplayContent? GetCachedReplay(int style, int track, int stage);

    /// <summary>
    ///     Returns the index of the finite replay frame whose Origin is closest to
    ///     <paramref name="position" />. Equal-distance candidates prefer the index nearest
    ///     <paramref name="preferredFrameIndex" />. Returns <c>-1</c> when no searchable replay
    ///     frame is cached or the query position is not finite. Only main and bonus runs (stage 0) are searchable.
    /// </summary>
    /// <param name="distanceSquared">
    ///     Squared distance between <paramref name="position" /> and the closest frame's origin,
    ///     or <see cref="float.PositiveInfinity" /> when no frame can be returned.
    /// </param>
    int FindClosestFrameIndex(int style, int track, int stage, in Vector position, int preferredFrameIndex,
                              out float distanceSquared);
}
