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
/// Checkpoint telemetry belonging to one stored run.
/// </summary>
public sealed class RunCheckpointDto
{
    /// <summary>Stable checkpoint identifier encoded as a decimal string.</summary>
    public required string Id { get; init; }

    /// <summary>Parent run identifier encoded as a decimal string.</summary>
    public required string RecordId { get; init; }

    public uint CheckpointIndex { get; init; }

    /// <summary>Elapsed checkpoint time, in microseconds.</summary>
    public long TimeMicros { get; init; }

    public float Sync { get; init; }

    public float VelocityStartX { get; init; }
    public float VelocityStartY { get; init; }
    public float VelocityStartZ { get; init; }

    public float VelocityAvgX { get; init; }
    public float VelocityAvgY { get; init; }
    public float VelocityAvgZ { get; init; }

    public float VelocityMaxX { get; init; }
    public float VelocityMaxY { get; init; }
    public float VelocityMaxZ { get; init; }

    public float VelocityEndX { get; init; }
    public float VelocityEndY { get; init; }
    public float VelocityEndZ { get; init; }
}
