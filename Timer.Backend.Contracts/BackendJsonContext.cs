/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or (at your
 * option) any later version.
 */

using System.Text.Json.Serialization;

namespace Source2Surf.Timer.Backend.Contracts;

/// <summary>
/// Source-generated JSON metadata for every v1 DTO and collection root.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(MapProfileDto))]
[JsonSerializable(typeof(MapProfileDto[]))]
[JsonSerializable(typeof(RunCheckpointDto))]
[JsonSerializable(typeof(RunCheckpointDto[]))]
[JsonSerializable(typeof(RunRecordDto))]
[JsonSerializable(typeof(RunRecordDto[]))]
[JsonSerializable(typeof(RecordListResponse))]
[JsonSerializable(typeof(RecordListResponse[]))]
[JsonSerializable(typeof(MapListResponse))]
[JsonSerializable(typeof(MapListResponse[]))]
[JsonSerializable(typeof(ApiErrorDto))]
[JsonSerializable(typeof(ApiErrorDto[]))]
[JsonSerializable(typeof(PointsRankDto))]
[JsonSerializable(typeof(PlayerMapStatsDto))]
[JsonSerializable(typeof(HealthStatusDto))]
[JsonSerializable(typeof(WorkerHealthStatusDto))]
public sealed partial class BackendJsonContext : JsonSerializerContext
{
}
