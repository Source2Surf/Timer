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

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
///     A point in a player's replay recording that a segmented run can rewind to: the frame count plus the stage
///     bookkeeping at that moment. It only describes the recording while <see cref="Lineage"/> still matches, since
///     trimming frames off the front (a timer start, idle trimming) moves the recording to a new lineage.
/// </summary>
internal sealed record ReplayMark(int Lineage, int FrameCount, int[] NewStageTicks, int[] StageTimerStartTicks);

internal enum ReplayRewindResult
{
    Rewound,

    /// <summary>The frames up to the mark are no longer recorded, so the run can't be replayed from there.</summary>
    Lost,

    /// <summary>A stage finish is mid-capture this frame; rewinding now would pull frames out from under it.</summary>
    Busy,
}

/// <summary>
///     Lets segmented practice rewind the replay recording along with the player, so a segmented run's replay holds
///     only the attempt that was kept.
/// </summary>
internal interface IReplayRewind
{
    /// <summary>Where the player's recording stands now, or null when they aren't being recorded.</summary>
    ReplayMark? GetReplayMark(PlayerSlot slot);

    /// <summary>
    ///     Cut the player's recording back to <paramref name="mark"/>. On success the run continues as a new replay
    ///     attempt, and any mark taken after this one no longer describes the recording.
    /// </summary>
    ReplayRewindResult TryRewind(PlayerSlot slot, ReplayMark mark);
}
