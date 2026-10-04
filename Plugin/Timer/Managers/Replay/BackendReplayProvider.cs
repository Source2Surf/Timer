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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Replay;

/// <summary>
/// Replay files live in the configured replay storage; the backend keeps which run each one belongs to.
/// </summary>
internal sealed class BackendReplayProvider : IReplayProvider
{
    private readonly IReplayCatalog                 _catalog;
    private readonly IReplayStorage                 _replayStorage;
    private readonly ILogger<BackendReplayProvider> _logger;

    public BackendReplayProvider(IReplayCatalog                 catalog,
                                 IReplayStorage                 replayStorage,
                                 ILogger<BackendReplayProvider> logger)
    {
        _catalog       = catalog;
        _replayStorage = replayStorage;
        _logger        = logger;
    }

    public async Task<byte[]?> GetReplayAsync(string mapName, int style, int track, ulong? steamId = null)
        => await DownloadAsync(await _catalog.GetReplayUrlAsync(mapName, false, style, track, 0, steamId));

    public async Task<byte[]?> GetStageReplayAsync(string mapName, int style, int track, int stage, ulong? steamId = null)
        => await DownloadAsync(await _catalog.GetReplayUrlAsync(mapName, true, style, track, stage, steamId));

    public async Task<byte[]?> GetRunReplayAsync(ulong runId)
        => await DownloadAsync(await _catalog.GetRunReplayUrlAsync(runId));

    public Task<IReadOnlyCollection<ulong>> GetStoredRunIdsAsync(IReadOnlyList<ulong> runIds)
        => _catalog.GetStoredReplayRunIdsAsync(runIds);

    public Task UploadReplayAsync(string mapName, int style, int track, ulong steamId, ulong runId, byte[] replayData)
    {
        // Each attempt owns an immutable object key. A partial retry must not truncate an
        // object referenced by an earlier successful commit.
        var key = $"{mapName.ToLowerInvariant()}/style_{style}/{track}/{steamId}_{runId}_{Guid.NewGuid():N}.replay";
        return UploadAsync(key, mapName, steamId, runId, replayData);
    }

    public Task UploadStageReplayAsync(string mapName, int style, int track, int stage, ulong steamId, ulong runId, byte[] replayData)
    {
        var key = $"{mapName.ToLowerInvariant()}/style_{style}/{track}/stage_{stage}/{steamId}_{runId}_{Guid.NewGuid():N}.replay";
        return UploadAsync(key, mapName, steamId, runId, replayData);
    }

    private async Task<byte[]?> DownloadAsync(string? replayUrl)
    {
        if (string.IsNullOrEmpty(replayUrl))
            return null;

        try
        {
            return await _replayStorage.DownloadAsync(replayUrl);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to download replay from {url}", replayUrl);
            return null;
        }
    }

    private async Task UploadAsync(string key, string mapName, ulong steamId, ulong runId, byte[] replayData)
    {
        string url;

        try
        {
            url = await _replayStorage.UploadAsync(key, replayData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay upload to storage failed for key {Key}", key);
            throw;
        }

        bool saved;

        try
        {
            saved = await _catalog.SaveReplayUrlAsync(mapName, steamId, runId, url);
        }
        catch (Exception ex)
        {
            // The backend may have committed even though its reply was lost. Deleting now could remove
            // the very object it points at, so keep the upload.
            _logger.LogError(ex,
                "Saving replay {url} for run {runId} failed; keeping the upload because it may already be in use.",
                url, runId);
            throw;
        }

        if (!saved)
        {
            // The run was removed while the upload was in progress.
            await _replayStorage.DeleteAsync(url);
        }
    }
}
