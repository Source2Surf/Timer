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
using System.Collections.Generic;
using Sharp.Shared.Units;

namespace SurfTimer.Modules.Replay;

internal class PlayerFrameData
{
    public          SteamID SteamId { get; init; }
    public required string  Name    { get; set; }

    public int   TimerStartFrame  { get; set; } = 0;
    public int   TimerFinishFrame { get; set; } = 0;
    public float FinishTime       { get; set; } = 0;
    public int   Style            { get; set; }
    public int   Track            { get; set; }

    public List<int> NewStageTicks        { get; } = [];
    public List<int> StageTimerStartTicks { get; } = [];

    public bool GrabbingPostFrame { get; set; } = false;
    public bool IsSavingReplay    { get; set; } = false;

    public List<ReplayFrameData> Frames { get; init; } = [];

    public Guid? PostFrameTimer      { get; set; } = null;
    public Guid? StagePostFrameTimer { get; set; } = null;
}
