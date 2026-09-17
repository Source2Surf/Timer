/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 */

namespace Source2Surf.Timer.Backend.Contracts;

/// <summary>
/// Public map metadata returned by the v1 read API.
/// </summary>
public sealed class MapProfileDto
{
    /// <summary>Stable database identifier encoded as an unsigned decimal string.</summary>
    public required string MapId { get; init; }

    /// <summary>The canonical map file/name key.</summary>
    public required string MapName { get; init; }

    public int Stages { get; init; }

    public int Bonuses { get; init; }

    /// <summary>
    /// Per-track tiers. Index zero is the main track; the remaining indexes are
    /// reserved for the other supported tracks.
    /// </summary>
    public required byte[] Tier { get; init; }

    /// <summary>Total recorded play time, in microseconds.</summary>
    public long TotalPlayTimeMicros { get; init; }

    public int PlayCount { get; init; }
}
