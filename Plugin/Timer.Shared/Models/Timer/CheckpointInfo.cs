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

namespace Source2Surf.Timer.Shared.Models.Timer;

public class CheckpointInfo
{
    public uint TimerTick { get; set; }

    public float Time => TimerTick * TimerConstants.TickInterval;

    public float Sync { get; set; }

    public Vector StartVelocity   { get; set; }
    public Vector AverageVelocity { get; set; }
    public Vector MaxVelocity     { get; set; }
    public Vector EndVelocity     { get; set; }
}
