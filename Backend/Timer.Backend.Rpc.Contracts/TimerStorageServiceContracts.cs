/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using MagicOnion;
using MessagePack;

namespace Source2Surf.Timer.Backend.Rpc.Contracts
{

[MessagePackObject]
public sealed class VectorDto
{
    [Key(0)]
    public float X { get; set; }

    [Key(1)]
    public float Y { get; set; }

    [Key(2)]
    public float Z { get; set; }
}

[MessagePackObject]
public sealed class MapInfoDto
{
    [Key(0)]
    public ulong MapId { get; set; }

    [Key(1)]
    public string MapName { get; set; } = string.Empty;

    [Key(2)]
    public ulong WorkshopId { get; set; }

    [Key(3)]
    public int Stages { get; set; }

    [Key(4)]
    public int Bonuses { get; set; }

    [Key(5)]
    public byte[] Tier { get; set; } = Array.Empty<byte>();

    [Key(6)]
    public float TotalPlayTime { get; set; }

    [Key(7)]
    public int PlayCount { get; set; }
}

/// <summary>
/// One leaderboard row, flat so that a board of thousands is one object per record on each side.
/// </summary>
[MessagePackObject]
public sealed class RecordDto
{
    [Key(0)]
    public long Id { get; set; }

    [Key(1)]
    public long RunDateUnixTimeMilliseconds { get; set; }

    [Key(2)]
    public ulong SteamId { get; set; }

    [Key(3)]
    public string PlayerName { get; set; } = string.Empty;

    [Key(4)]
    public ulong MapId { get; set; }

    [Key(5)]
    public int Style { get; set; }

    [Key(6)]
    public int Track { get; set; }

    [Key(7)]
    public int Stage { get; set; }

    [Key(8)]
    public float Time { get; set; }

    [Key(9)]
    public int Jumps { get; set; }

    [Key(10)]
    public int Strafes { get; set; }

    [Key(11)]
    public float Sync { get; set; }

    [Key(12)]
    public float StartX { get; set; }

    [Key(13)]
    public float StartY { get; set; }

    [Key(14)]
    public float StartZ { get; set; }

    [Key(15)]
    public float AverageX { get; set; }

    [Key(16)]
    public float AverageY { get; set; }

    [Key(17)]
    public float AverageZ { get; set; }

    [Key(18)]
    public float EndX { get; set; }

    [Key(19)]
    public float EndY { get; set; }

    [Key(20)]
    public float EndZ { get; set; }
}

[MessagePackObject]
public sealed class RecordCheckpointDto
{
    [Key(0)]
    public long Id { get; set; }

    [Key(1)]
    public long RecordId { get; set; }

    [Key(2)]
    public uint Index { get; set; }

    [Key(3)]
    public float Time { get; set; }

    [Key(4)]
    public float Sync { get; set; }

    [Key(5)]
    public MotionDto Motion { get; set; } = new MotionDto();
}

[MessagePackObject]
public sealed class ZoneDto
{
    [Key(0)]
    public ulong Id { get; set; }

    [Key(1)]
    public int Type { get; set; }

    [Key(2)]
    public int Track { get; set; }

    [Key(3)]
    public int Sequence { get; set; }

    [Key(4)]
    public VectorDto Mins { get; set; } = new VectorDto();

    [Key(5)]
    public VectorDto Maxs { get; set; } = new VectorDto();

    [Key(6)]
    public VectorDto Center { get; set; } = new VectorDto();

    [Key(7)]
    public VectorDto? TeleportOrigin { get; set; }

    [Key(8)]
    public VectorDto? TeleportAngles { get; set; }

    [Key(9)]
    public string? Config { get; set; }
}

[MessagePackObject]
public sealed class StyleSummaryDto
{
    [Key(0)]
    public int Style { get; set; }

    [Key(1)]
    public int MapsCompleted { get; set; }

    [Key(2)]
    public int BonusesCompleted { get; set; }

    [Key(3)]
    public int MapRecords { get; set; }

    [Key(4)]
    public int BonusRecords { get; set; }

    [Key(5)]
    public int StageRecords { get; set; }
}

[MessagePackObject]
public sealed class PlayerSummaryDto
{
    [Key(0)]
    public int TotalMaps { get; set; }

    [Key(1)]
    public int TotalBonuses { get; set; }

    [Key(2)]
    public float PlayTime { get; set; }

    [Key(3)]
    public StyleSummaryDto[] Styles { get; set; } = Array.Empty<StyleSummaryDto>();
}

[MessagePackObject]
public sealed class RankDto
{
    [Key(0)]
    public int Rank { get; set; }

    [Key(1)]
    public int Total { get; set; }
}

[MessagePackObject]
public sealed class MapStatsDto
{
    [Key(0)]
    public float PlayTime { get; set; }

    [Key(1)]
    public int PlayCount { get; set; }
}

/// <summary>
/// Score jobs queued by a tier change or recalculation. The scores themselves update asynchronously.
/// </summary>
/// <summary>
/// A run deleted by an admin, so the game server can drop its replay files and reload what it showed.
/// </summary>
[MessagePackObject]
public sealed class DeletedRunDto
{
    [Key(0)]
    public ulong RunId { get; set; }

    [Key(1)]
    public ulong SteamId { get; set; }

    [Key(2)]
    public RunKind Kind { get; set; }

    [Key(3)]
    public int Style { get; set; }

    [Key(4)]
    public int Track { get; set; }

    [Key(5)]
    public int Stage { get; set; }

    // It was the player's best, so its board changed.
    [Key(6)]
    public bool WasBest { get; set; }

    [Key(7)]
    public string[] ReplayUrls { get; set; } = Array.Empty<string>();
}

[MessagePackObject]
public sealed class ScoreJobsDto
{
    [Key(0)]
    public bool MapFound { get; set; }

    [Key(1)]
    public int MapsAffected { get; set; }

    [Key(2)]
    public int BoardsQueued { get; set; }

    [Key(3)]
    public int DeadLettersRequeued { get; set; }

    [Key(4)]
    public int DisabledStyleBoardsSkipped { get; set; }

    [Key(5)]
    public byte? PreviousTier { get; set; }

    [Key(6)]
    public byte? CurrentTier { get; set; }

    [Key(7)]
    public string[] FailedMaps { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Everything a game server reads or writes apart from run submissions. A map is named by its file
/// name plus its Steam Workshop item (0 when it isn't from the workshop), so its data follows the
/// item when an update renames the map.
/// </summary>
public interface ITimerStorageServiceV1 : IService<ITimerStorageServiceV1>
{
    /// <summary>Gets the map, adding it when it's new.</summary>
    UnaryResult<MapInfoDto> GetMapInfoAsync(string mapName, ulong workshopId);

    UnaryResult<MapInfoDto[]> GetMapProfilesAsync();

    UnaryResult<string[]> GetAllMapNamesAsync();

    UnaryResult IncrementMapStatsAsync(string mapName, ulong workshopId, float deltaSeconds);

    /// <summary>Sets the main track's tier and queues its score boards, under the backend's score policy.</summary>
    UnaryResult<ScoreJobsDto> SetMapTierAsync(string mapName, ulong workshopId, byte tier);

    /// <summary>Queues a map's score boards under the backend's score policy; every map when mapName is null.</summary>
    UnaryResult<ScoreJobsDto> RecalculateScoresAsync(string? mapName, ulong workshopId);

    /// <summary>The map's leaderboards: every board of the kind, or the one named by style, track and stage.</summary>
    UnaryResult<RecordDto[]> GetMapRecordsAsync(string mapName,
                                                ulong  workshopId,
                                                RunKind kind,
                                                bool   allBoards,
                                                int    style,
                                                int    track,
                                                int    stage,
                                                int    limit);

    UnaryResult<RecordDto[]> GetPlayerRecordsAsync(ulong steamId, string mapName, ulong workshopId, RunKind kind);

    UnaryResult<RecordDto[]> GetRecentRecordsAsync(string mapName, ulong workshopId, ulong steamId, int limit);

    UnaryResult<RecordDto[]> GetPlayerRunsAsync(string mapName,
                                                ulong  workshopId,
                                                ulong  steamId,
                                                int    style,
                                                int    track,
                                                int    stage,
                                                int    limit);

    UnaryResult<RecordCheckpointDto[]> GetRecordCheckpointsAsync(long recordId);

    UnaryResult RemoveMapRecordsAsync(string mapName, ulong workshopId);

    /// <summary>Deletes one run of the map with its replay rows; null when it is not on this map.</summary>
    UnaryResult<DeletedRunDto?> DeleteRunAsync(string mapName, ulong workshopId, ulong runId);

    UnaryResult<PlayerSummaryDto?> GetPlayerSummaryAsync(ulong steamId);

    UnaryResult<Dictionary<ulong, float>> GetCompletedMapsAsync(ulong steamId, int style, int track);

    UnaryResult<RankDto> GetPlayerPointsRankAsync(ulong steamId);

    UnaryResult UpdatePlayerMapStatsAsync(ulong steamId, string mapName, ulong workshopId, float deltaSeconds);

    UnaryResult<MapStatsDto> GetPlayerMapStatsAsync(ulong steamId, string mapName, ulong workshopId);

    /// <summary>The player's settings in the game server's binary format, or null when they're all defaults.</summary>
    UnaryResult<byte[]?> GetPlayerSettingsAsync(ulong steamId);

    /// <summary>Replaces the player's settings; empty data resets them to defaults.</summary>
    UnaryResult SavePlayerSettingsAsync(ulong steamId, byte[] data);

    UnaryResult<ZoneDto[]> GetZonesAsync(string mapName, ulong workshopId);

    UnaryResult SaveZonesAsync(string mapName, ulong workshopId, ZoneDto[] zones);

    /// <summary>The fastest run's replay URL on a leaderboard, or the player's own when steamId is set.</summary>
    UnaryResult<string?> GetReplayUrlAsync(string mapName,
                                           ulong  workshopId,
                                           RunKind kind,
                                           int    style,
                                           int    track,
                                           int    stage,
                                           ulong? steamId);

    UnaryResult<string?> GetRunReplayUrlAsync(ulong runId);

    UnaryResult<ulong[]> GetStoredReplayRunIdsAsync(ulong[] runIds);

    /// <summary>Points a run at its uploaded replay. False when the run no longer exists.</summary>
    UnaryResult<bool> SaveReplayUrlAsync(string mapName, ulong workshopId, ulong steamId, ulong runId, string url);
}

}
