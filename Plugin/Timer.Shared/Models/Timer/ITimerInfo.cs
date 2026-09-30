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
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Shared.Models.Timer;

public interface ITimerInfo
{
    ETimerStatus Status { get; }

    int    Jumps         { get; }
    int    Strafes       { get; }
    float  Time          { get; }
    Vector StartVelocity { get; }
    Vector AvgVelocity   { get; }
    Vector EndVelocity   { get; }
    Vector MaxVelocity   { get; }
    float  Sync          { get; }

    EZoneType                     InZone      { get; }
    int                           Track       { get; }
    int                           Style       { get; }
    int                           Checkpoint  { get; }
    IReadOnlyList<CheckpointInfo> Checkpoints { get; }

    void ChangeStyle(int style);

    void ChangeTrack(int track);
}
