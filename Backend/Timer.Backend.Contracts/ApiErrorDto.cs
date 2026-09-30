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
/// Stable machine-readable error envelope for failed HTTP requests.
/// </summary>
public sealed class ApiErrorDto
{
    public string ApiVersion { get; init; } = ApiVersions.Current;

    /// <summary>Stable, lower-case machine-readable error code.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable diagnostic text; clients must not branch on it.</summary>
    public required string Message { get; init; }

    /// <summary>Optional request correlation identifier.</summary>
    public string? RequestId { get; init; }

    /// <summary>Optional non-sensitive validation/detail messages.</summary>
    public string[]? Details { get; init; }
}
