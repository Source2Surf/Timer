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
/// Versioned envelope for the map catalog.
/// </summary>
public sealed class MapListResponse
{
    public string ApiVersion { get; init; } = ApiVersions.Current;

    /// <summary>
    /// Canonical map keys. Profiles are fetched individually so listing maps remains a
    /// single projected database query instead of an N+1 profile/track-tier load.
    /// </summary>
    public required string[] MapNames { get; init; }
}
