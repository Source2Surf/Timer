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

namespace Timer.MapChooser.Logic;

/// <summary>
///     How long the map runs, in game time.
/// </summary>
internal sealed class MapClock
{
    public float StartedAt    { get; private set; }
    public float LimitSeconds { get; private set; }
    public int   Extends      { get; private set; }

    public void Start(float now, float limitMinutes)
    {
        StartedAt    = now;
        LimitSeconds = limitMinutes * 60f;
        Extends      = 0;
    }

    public float TimeLeft(float now)
        => StartedAt + LimitSeconds - now;

    public float Elapsed(float now)
        => now - StartedAt;

    /// <param name="counts">Whether it uses up one of the map's extends; an admin's doesn't.</param>
    public void Extend(float minutes, bool counts = true)
    {
        LimitSeconds += minutes * 60f;

        if (counts)
        {
            Extends++;
        }
    }
}
