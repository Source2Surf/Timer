/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Managers.Request;

/// <summary>
/// Where the replays of runs are stored, kept by the backend next to the runs.
/// </summary>
internal interface IReplayCatalog
{
    Task<string?> GetReplayUrlAsync(string mapName, bool stageRun, int style, int track, int stage, ulong? steamId);

    Task<string?> GetRunReplayUrlAsync(ulong runId);

    Task<IReadOnlyCollection<ulong>> GetStoredReplayRunIdsAsync(IReadOnlyList<ulong> runIds);

    /// <summary>Points a run at its uploaded replay. False when the run no longer exists.</summary>
    Task<bool> SaveReplayUrlAsync(string mapName, ulong steamId, ulong runId, string url);
}

/// <summary>
/// The timer's storage, on the backend's gRPC storage service. Each call gets the configured deadline.
/// </summary>
internal sealed class BackendRequestManager : IRequestManager, IReplayCatalog
{
    // Keeps a request well under the backend's 64 KiB request limit.
    private const int StoredReplayChunkSize = 1000;

    private readonly ITimerStorageServiceV1 _client;
    private readonly TimeSpan               _deadline;
    private readonly CancellationToken      _stopping;
    private volatile WorkshopBinding?       _workshop;

    public BackendRequestManager(BackendChannel channel, BackendOptions options, InterfaceBridge bridge)
        : this(channel.CreateClient<ITimerStorageServiceV1>(), options, bridge.CancellationToken)
    {
    }

    internal BackendRequestManager(ITimerStorageServiceV1 client, BackendOptions options, CancellationToken stopping)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);

        _client   = client;
        _deadline = options.RpcDeadline;
        _stopping = stopping;
    }

    private ITimerStorageServiceV1 Call
        => _client.WithOptions(new CallOptions(deadline: DateTime.UtcNow + _deadline, cancellationToken: _stopping));

    // Only the map being played has a workshop item bound.
    private ulong WorkshopId(string mapName)
        => _workshop is { } workshop && string.Equals(workshop.MapName, mapName, StringComparison.OrdinalIgnoreCase)
               ? workshop.WorkshopId
               : 0;

#region MapInfo

    public void SetMapWorkshopId(string mapName, ulong workshopId)
        => _workshop = workshopId == 0 ? null : new WorkshopBinding(mapName, workshopId);

    public async Task<MapProfile> GetMapInfo(string map)
        => BackendRpcMapper.ToProfile(await Call.GetMapInfoAsync(map, WorkshopId(map)));

    public async Task<ScoreQueueResult> SetMapTierAsync(string mapName, byte tier)
        => BackendRpcMapper.ToResult(await Call.SetMapTierAsync(mapName, WorkshopId(mapName), tier));

    public async Task IncrementMapStatsAsync(string mapName, float deltaSeconds)
        => await Call.IncrementMapStatsAsync(mapName, WorkshopId(mapName), deltaSeconds);

    public async Task<IReadOnlyList<string>> GetAllMapNamesAsync()
        => await Call.GetAllMapNamesAsync();

    public async Task<IReadOnlyList<MapProfile>> GetMapProfilesAsync()
        => BackendRpcMapper.ToProfiles(await Call.GetMapProfilesAsync());

#endregion

#region Record

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => GetMapRecordsAsync(mapName, RunKind.Main, true, 0, 0, 0, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = IRequestManager.DefaultRecordLimit)
        => GetMapRecordsAsync(mapName, RunKind.Stage, true, 0, 0, 0, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                        int    style,
                                                        int    track,
                                                        int    limit = IRequestManager.DefaultRecordLimit)
        => GetMapRecordsAsync(mapName, RunKind.Main, false, style, track, 0, limit);

    public Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                             int    style,
                                                             int    track,
                                                             int    stage,
                                                             int    limit = IRequestManager.DefaultRecordLimit)
        => GetMapRecordsAsync(mapName, RunKind.Stage, false, style, track, stage, limit);

    private async Task<IReadOnlyList<RunRecord>> GetMapRecordsAsync(string  mapName,
                                                                    RunKind kind,
                                                                    bool    allBoards,
                                                                    int     style,
                                                                    int     track,
                                                                    int     stage,
                                                                    int     limit)
        => BackendRpcMapper.ToRecords(await Call.GetMapRecordsAsync(mapName, WorkshopId(mapName), kind, allBoards,
                                                                    style, track, stage, limit));

    public async Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName)
        => BackendRpcMapper.ToRecords(await Call.GetPlayerRecordsAsync(steamId.AsPrimitive(), mapName,
                                                                       WorkshopId(mapName), RunKind.Main));

    public async Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName)
        => BackendRpcMapper.ToRecords(await Call.GetPlayerRecordsAsync(steamId.AsPrimitive(), mapName,
                                                                       WorkshopId(mapName), RunKind.Stage));

    public async Task RemoveMapRecords(string mapName)
        => await Call.RemoveMapRecordsAsync(mapName, WorkshopId(mapName));

    public async Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId)
        => BackendRpcMapper.ToCheckpoints(await Call.GetRecordCheckpointsAsync(recordId));

    public async Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10)
        => BackendRpcMapper.ToRecords(await Call.GetRecentRecordsAsync(mapName, WorkshopId(mapName),
                                                                       steamId.AsPrimitive(), limit));

    public async Task<IReadOnlyList<RunRecord>> GetPlayerRuns(string  mapName,
                                                              SteamID steamId,
                                                              int     style,
                                                              int     track,
                                                              int     stage,
                                                              int     limit = 10)
        => BackendRpcMapper.ToRecords(await Call.GetPlayerRunsAsync(mapName, WorkshopId(mapName), steamId.AsPrimitive(),
                                                                    style, track, stage, limit));

    public async Task<PlayerSummary?> GetPlayerSummary(SteamID steamId)
        => await Call.GetPlayerSummaryAsync(steamId.AsPrimitive()) is { } summary
               ? BackendRpcMapper.ToSummary(summary)
               : null;

    public async Task<IReadOnlyDictionary<ulong, float>> GetCompletedMapsAsync(SteamID steamId, int style, int track)
        => await Call.GetCompletedMapsAsync(steamId.AsPrimitive(), style, track);

#endregion

#region Score

    public async Task<ScoreQueueResult> RecalculateMapScoresAsync(string? mapName)
        => BackendRpcMapper.ToResult(await Call.RecalculateScoresAsync(mapName, mapName is null ? 0 : WorkshopId(mapName)));

#endregion

#region Zone

    public async Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName)
        => BackendRpcMapper.ToZones(await Call.GetZonesAsync(mapName, WorkshopId(mapName)));

    public async Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones)
        => await Call.SaveZonesAsync(mapName, WorkshopId(mapName), BackendRpcMapper.ToDtos(zones));

#endregion

    public async Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId)
    {
        var rank = await Call.GetPlayerPointsRankAsync(steamId.AsPrimitive());

        return (rank.Rank, rank.Total);
    }

    public async Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds)
        => await Call.UpdatePlayerMapStatsAsync(steamId.AsPrimitive(), mapName, WorkshopId(mapName), deltaSeconds);

    public async Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
    {
        var stats = await Call.GetPlayerMapStatsAsync(steamId.AsPrimitive(), mapName, WorkshopId(mapName));

        return (stats.PlayTime, stats.PlayCount);
    }

#region Replay

    public async Task<string?> GetReplayUrlAsync(string mapName, bool stageRun, int style, int track, int stage, ulong? steamId)
        => await Call.GetReplayUrlAsync(mapName, WorkshopId(mapName), stageRun ? RunKind.Stage : RunKind.Main,
                                        style, track, stage, steamId);

    public async Task<string?> GetRunReplayUrlAsync(ulong runId)
        => await Call.GetRunReplayUrlAsync(runId);

    public async Task<IReadOnlyCollection<ulong>> GetStoredReplayRunIdsAsync(IReadOnlyList<ulong> runIds)
    {
        var stored = new List<ulong>();

        for (var start = 0; start < runIds.Count; start += StoredReplayChunkSize)
        {
            var chunk = new ulong[Math.Min(StoredReplayChunkSize, runIds.Count - start)];

            for (var i = 0; i < chunk.Length; i++)
            {
                chunk[i] = runIds[start + i];
            }

            stored.AddRange(await Call.GetStoredReplayRunIdsAsync(chunk));
        }

        return stored;
    }

    public async Task<bool> SaveReplayUrlAsync(string mapName, ulong steamId, ulong runId, string url)
        => await Call.SaveReplayUrlAsync(mapName, WorkshopId(mapName), steamId, runId, url);

#endregion

    private sealed record WorkshopBinding(string MapName, ulong WorkshopId);
}
