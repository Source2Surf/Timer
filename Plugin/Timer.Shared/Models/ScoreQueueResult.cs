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
/// Score recalculation queued by a tier change or a recalculation request. The scores update in the background.
/// </summary>
/// <param name="MapFound">False when the named map doesn't exist; nothing was queued.</param>
/// <param name="FailedMaps">For every map: the maps that couldn't be queued, each as "map: reason".</param>
public sealed record ScoreQueueResult(bool                  MapFound,
                                      int                   MapsAffected,
                                      int                   BoardsQueued,
                                      IReadOnlyList<string> FailedMaps);
