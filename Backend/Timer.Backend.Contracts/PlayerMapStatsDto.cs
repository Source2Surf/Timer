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

/// <summary>Aggregated player activity for one map.</summary>
public sealed class PlayerMapStatsDto
{
    public string ApiVersion { get; init; } = ApiVersions.Current;

    /// <summary>Steam64 identifier encoded as an unsigned decimal string.</summary>
    public required string SteamId { get; init; }

    public required string MapName { get; init; }

    /// <summary>Total recorded play time, in microseconds.</summary>
    public long PlayTimeMicros { get; init; }

    public int PlayCount { get; init; }
}
