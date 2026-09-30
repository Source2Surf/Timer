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

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Replay;

internal sealed class ReplayProviderProxy
{
    private readonly ISharedSystem                    _shared;
    private readonly ILogger<ReplayProviderProxy>     _logger;
    private          IReplayProvider?                 _provider;

    public bool IsAvailable => Volatile.Read(ref _provider) is not null;

    public bool UploadNonPersonalBest => Volatile.Read(ref _provider)?.UploadNonPersonalBest ?? false;

    public ReplayProviderProxy(ISharedSystem shared, ILogger<ReplayProviderProxy> logger)
    {
        _shared   = shared;
        _logger   = logger;
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
        else
        {
            Volatile.Write(ref _provider, null);
            _logger.LogWarning("No external IReplayProvider found, remote replay disabled");
        }
    }

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
