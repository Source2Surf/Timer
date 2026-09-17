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
/// Public leaderboard/run representation returned by the v1 read API.
/// </summary>
public sealed class RunRecordDto
{
    /// <summary>Stable run identifier encoded as a decimal string.</summary>
    public required string Id { get; init; }

    /// <summary>Run creation time as Unix milliseconds in UTC.</summary>
    public long RunDate { get; init; }

    /// <summary>Steam64 identifier encoded as an unsigned decimal string.</summary>
    public required string SteamId { get; init; }

    /// <summary>Player name at the time the response is materialized.</summary>
    public string? PlayerName { get; init; }

    /// <summary>Stable map identifier encoded as a decimal string.</summary>
    public required string MapId { get; init; }

    public int Style { get; init; }
    public int Track { get; init; }
    public int Stage { get; init; }

    /// <summary>Elapsed run time, in microseconds.</summary>
    public long TimeMicros { get; init; }

    public int Jumps { get; init; }
    public int Strafes { get; init; }
    public float Sync { get; init; }

    public float VelocityStartX { get; init; }
    public float VelocityStartY { get; init; }
    public float VelocityStartZ { get; init; }

    public float VelocityAvgX { get; init; }
    public float VelocityAvgY { get; init; }
    public float VelocityAvgZ { get; init; }

    public float VelocityEndX { get; init; }
    public float VelocityEndY { get; init; }
    public float VelocityEndZ { get; init; }

    /// <summary>
    /// Checkpoints are omitted when the endpoint does not request telemetry.
    /// </summary>
    public RunCheckpointDto[]? Checkpoints { get; init; }
}
