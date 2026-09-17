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
/// Versioned envelope for map leaderboard records.
/// </summary>
public sealed class RecordListResponse
{
    public string ApiVersion { get; init; } = ApiVersions.Current;

    /// <summary>The canonical map key used for this query.</summary>
    public required string MapName { get; init; }

    public required RunRecordDto[] Records { get; init; }
}
