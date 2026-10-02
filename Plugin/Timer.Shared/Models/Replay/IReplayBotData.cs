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
using Sharp.Shared.Units;

namespace Source2Surf.Timer.Shared.Models.Replay;

public interface IReplayBotData
{
    ReplayBotConfig                Config       { get; }
    PlayerSlot                     Slot         { get; }
    int                            Track        { get; }
    int                            Style        { get; }
    int                            Stage        { get; }
    float                          Time         { get; }
    ReplayFileHeader?              Header       { get; }
    IReadOnlyList<ReplayFrameData> Frames       { get; }
    int                            CurrentFrame { get; }
    EReplayBotStatus               Status       { get; }
    EReplayBotType                 Type         { get; }

    /// <summary>
    ///     The replay's place on its leaderboard: 1 is the server record, 0 a player's own run that isn't their best.
    /// </summary>
    int Rank { get; }

    /// <summary>
    ///     The run a central bot plays, when known.
    /// </summary>
    long RunId { get; }

    /// <summary>
    ///     A central bot's playback, set by the player who started it: held on its frame, and frames played per tick.
    /// </summary>
    bool  Paused { get; }
    float Speed  { get; }

    /// <summary>
    ///     The player who started a central bot's replay, and so has its controls.
    /// </summary>
    PlayerSlot? Owner { get; }
}
