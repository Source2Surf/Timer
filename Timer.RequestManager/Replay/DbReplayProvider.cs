using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;
using Timer.RequestManager.Storage;

namespace Timer.RequestManager.Replay;

internal sealed class DbReplayProvider : IReplayProvider
{
    private readonly StorageServiceImpl        _storage;
    private readonly IReplayStorage            _replayStorage;
    private readonly ILogger<DbReplayProvider> _logger;

    public bool UploadNonPersonalBest { get; }

    public DbReplayProvider(StorageServiceImpl        storage,
                            IReplayStorage            replayStorage,
                            bool                      uploadNonPersonalBest,
                            ILogger<DbReplayProvider> logger)
    {
        _storage              = storage;
        _replayStorage        = replayStorage;
        UploadNonPersonalBest = uploadNonPersonalBest;
        _logger               = logger;
    }

    public Task<byte[]?> GetReplayAsync(string mapName, int style, int track, ulong? steamId = null)
        => GetReplayCoreAsync(mapName, RunType.Main, style, track, 0, steamId);

    public Task<byte[]?> GetStageReplayAsync(string mapName, int style, int track, int stage, ulong? steamId = null)
        => GetReplayCoreAsync(mapName, RunType.Stage, style, track, stage, steamId);

    public Task UploadReplayAsync(string mapName, int style, int track, ulong steamId, ulong runId, byte[] replayData)
    {
        var key = $"{mapName.ToLowerInvariant()}/style_{style}/{track}/{steamId}_{runId}.replay";
        return UploadReplayCoreAsync(key, mapName, steamId, runId, replayData);
    }

    public Task UploadStageReplayAsync(string mapName, int style, int track, int stage, ulong steamId, ulong runId, byte[] replayData)
    {
        var key = $"{mapName.ToLowerInvariant()}/style_{style}/{track}/stage_{stage}/{steamId}_{runId}.replay";
        return UploadReplayCoreAsync(key, mapName, steamId, runId, replayData);
    }

    private async Task<byte[]?> GetReplayCoreAsync(string mapName, RunType runType, int style, int track, int stage, ulong? steamId)
    {
        var mapId = await _storage.ResolveMapIdByNameAsync(mapName);
        if (mapId is null) return null;

        var query = _storage.Db.Queryable<ReplayEntity>()
            .InnerJoin<RunEntity>((r, run) => r.RunId == run.Id)
            .Where((r, run) => run.MapId == mapId.Value
                            && run.RunType == runType
                            && run.Style == style
                            && run.Track == (ushort)track
                            && run.Stage == (ushort)stage);

        if (steamId.HasValue)
        {
            var sid = unchecked((long)steamId.Value);
            query = query.Where((r, run) => r.SteamId == sid);
        }

        var replayUrl = await query
            .OrderBy((r, run) => run.Time)
            .OrderBy((r, run) => run.Id)
            .Select((r, run) => r.Replay)
            .FirstAsync();

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

    private async Task UploadReplayCoreAsync(string key, string mapName, ulong steamId, ulong runId, byte[] replayData)
    {
#if DEBUG
        _logger.LogInformation("DbReplayProvider.Upload start key={Key} bytes={Bytes} steamId={SteamId} runId={RunId}",
                               key, replayData.Length, steamId, runId);
#endif

        string url;
        try
        {
            url = await _replayStorage.UploadAsync(key, replayData);
#if DEBUG
            _logger.LogInformation("DbReplayProvider.Upload storage OK key={Key} url={Url}", key, url);
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DbReplayProvider.Upload storage FAILED key={Key}", key);
            throw;
        }

        try
        {
            var mapId = await _storage.EnsureMapIdByNameAsync(mapName);

            var entity = new ReplayEntity
            {
                MapId     = mapId,
                SteamId   = unchecked((long)steamId),
                RunId     = runId,
                Replay    = url,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            if (!await _storage.SaveReplayMetadataAsync(entity))
            {
                // The run was removed while the external upload was in progress.
                await _replayStorage.DeleteAsync(url);
                return;
            }

#if DEBUG
            _logger.LogInformation("DbReplayProvider.Upload DB OK key={Key} mapId={MapId} runId={RunId}", key, mapId, runId);
#endif
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DB write failed after uploading replay {url}, attempting cleanup", url);

            try
            {
                await _replayStorage.DeleteAsync(url);
            }
            catch (Exception cleanupEx)
            {
                _logger.LogWarning(cleanupEx, "Failed to clean up orphaned replay {url}", url);
            }

            throw;
        }
    }
}
