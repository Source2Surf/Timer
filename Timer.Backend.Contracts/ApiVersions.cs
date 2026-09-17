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
/// Version identifiers used by the HTTP contract and response envelopes.
/// </summary>
public static class ApiVersions
{
    /// <summary>The first version of the read-only HTTP contract.</summary>
    public const string V1 = "v1";

    /// <summary>The version currently emitted by this contract package.</summary>
    public const string Current = V1;
}
