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

/// <summary>Player position in the points leaderboard.</summary>
public sealed class PointsRankDto
{
    public string ApiVersion { get; init; } = ApiVersions.Current;

    /// <summary>Steam64 identifier encoded as an unsigned decimal string.</summary>
    public required string SteamId { get; init; }

    /// <summary>One-based rank, or zero when the player is not ranked.</summary>
    public int Rank { get; init; }

    /// <summary>Total ranked player count, or zero when the player is not ranked.</summary>
    public int Total { get; init; }
}
