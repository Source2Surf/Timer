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
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Replay;

/// <summary>
/// Remote replays: an external IReplayProvider module when one is loaded, otherwise the built-in one when
/// timer.jsonc sets replay:storage_base_url.
/// </summary>
internal sealed class ReplayProviderProxy : IDisposable
{
    private const string StorageBaseUrlKey = "replay:storage_base_url";

    private readonly ISharedSystem                    _shared;
    private readonly ILogger<ReplayProviderProxy>     _logger;
    private readonly HttpClient?                      _httpClient;
    private readonly BackendReplayProvider?           _builtIn;
    private          IReplayProvider?                 _provider;

    public bool IsAvailable => Volatile.Read(ref _provider) is not null;

    public ReplayProviderProxy(ISharedSystem                shared,
                               IConfiguration               configuration,
                               IReplayCatalog               catalog,
                               ILoggerFactory               loggerFactory,
                               ILogger<ReplayProviderProxy> logger)
    {
        _shared   = shared;
        _logger   = logger;

        if (configuration[StorageBaseUrlKey] is { } baseUrl && !string.IsNullOrWhiteSpace(baseUrl))
        {
            _httpClient = new HttpClient();
            _builtIn    = new BackendReplayProvider(catalog,
                                                    new HttpReplayStorage(_httpClient, baseUrl),
                                                    loggerFactory.CreateLogger<BackendReplayProvider>());
        }
    }

    public void RefreshProvider()
    {
        var external = _shared.GetSharpModuleManager()
                              .GetOptionalSharpModuleInterface<IReplayProvider>(IReplayProvider.Identity);

        if (external?.Instance is { } instance)
        {
            Volatile.Write(ref _provider, instance);
            _logger.LogInformation("Using external IReplayProvider");
        }
        else if (_builtIn is not null)
        {
            Volatile.Write(ref _provider, _builtIn);
            _logger.LogInformation("Using the replay storage at {key}", StorageBaseUrlKey);
        }
        else
        {
            Volatile.Write(ref _provider, null);
            _logger.LogWarning("{key} isn't set and no external IReplayProvider is loaded: remote replays are disabled", StorageBaseUrlKey);
        }
    }

    public void Dispose()
        => _httpClient?.Dispose();

    public async Task<byte[]?> GetReplayAsync(string mapName, int style, int track, ulong? steamId = null)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return null;

        return await provider.GetReplayAsync(mapName, style, track, steamId);
    }

    public async Task<byte[]?> GetStageReplayAsync(string mapName, int style, int track, int stage, ulong? steamId = null)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return null;

        return await provider.GetStageReplayAsync(mapName, style, track, stage, steamId);
    }

    public async Task<byte[]?> GetRunReplayAsync(ulong runId)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return null;

        return await provider.GetRunReplayAsync(runId);
    }

    /// <summary>Deletes a deleted run's replay from the replay storage; best effort.</summary>
    public Task DeleteStoredReplayAsync(string url)
        => _builtIn?.DeleteStoredAsync(url) ?? Task.CompletedTask;

    public async Task<IReadOnlyCollection<ulong>> GetStoredRunIdsAsync(IReadOnlyList<ulong> runIds)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return [];

        return await provider.GetStoredRunIdsAsync(runIds);
    }

    public async Task UploadReplayAsync(string mapName, int style, int track, ulong steamId, ulong runId, byte[] replayData)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return;

        await provider.UploadReplayAsync(mapName, style, track, steamId, runId, replayData);
    }

    public async Task UploadStageReplayAsync(string mapName, int style, int track, int stage, ulong steamId, ulong runId, byte[] replayData)
    {
        var provider = Volatile.Read(ref _provider);
        if (provider is null) return;

        await provider.UploadStageReplayAsync(mapName, style, track, stage, steamId, runId, replayData);
    }
}
