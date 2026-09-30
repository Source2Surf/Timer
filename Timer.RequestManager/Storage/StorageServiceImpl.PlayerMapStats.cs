using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds)
    {
        ValidatePlayTimeDelta(deltaSeconds);
        if (deltaSeconds == 0f) return;

        var steamIdValue = ToDbSteamId(steamId);
        var mapId = await EnsureMapIdByNameAsync(mapName);
        if (await IncrementPlayerMapStatsRowAsync(steamIdValue, mapId, deltaSeconds) == 1) return;

        // A guarded update may also mean the existing row is exhausted/corrupt.
        // Do not turn that into an insert or silently acknowledge a lost increment.
        if (await _db.Queryable<PlayerMapStatsEntity>()
                     .Where(x => x.SteamId == steamIdValue && x.MapId == mapId)
                     .AnyAsync(OperationCancellation))
        {
            throw new InvalidOperationException("Player map counters are outside the supported range.");
        }

        var entity = new PlayerMapStatsEntity
        {
            SteamId = steamIdValue, MapId = mapId, PlayTime = deltaSeconds, PlayCount = 1,
        };
        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync(OperationCancellation);
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            _logger.LogDebug(ex,
                "PlayerMapStats insert raced for {steamId} map {mapId}, retrying update.", steamId, mapId);
            if (await IncrementPlayerMapStatsRowAsync(steamIdValue, mapId, deltaSeconds) != 1)
                throw new InvalidOperationException("Player map counters could not be incremented.");
        }
    }

    private Task<int> IncrementPlayerMapStatsRowAsync(long steamId, ulong mapId, float deltaSeconds)
    {
        // Evaluate the bound in double: rounding a float subtraction upwards could
        // admit a sum beyond the maximum duration supported by the HTTP contract.
        var maximumCurrentTime = (double)MaximumStoredPlayTimeSeconds - deltaSeconds;
        return _db.Updateable<PlayerMapStatsEntity>()
                  .SetColumns(x => x.PlayTime == x.PlayTime + deltaSeconds)
                  .SetColumns(x => x.PlayCount == x.PlayCount + 1)
                  .Where(x => x.SteamId == steamId && x.MapId == mapId
                              && x.PlayCount >= 0 && x.PlayCount < int.MaxValue
                              && x.PlayTime >= 0 && x.PlayTime <= maximumCurrentTime)
                  .ExecuteCommandAsync(OperationCancellation);
    }

    public Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
        => GetPlayerMapStatsByDbIdAsync(ToDbSteamId(steamId), mapName);

    internal Task<(float playTime, int playCount)> GetPlayerMapStatsForReadApiAsync(ulong  steamId,
                                                                                    string mapName)
        => GetPlayerMapStatsByDbIdAsync(unchecked((long)steamId), mapName);

    private async Task<(float playTime, int playCount)> GetPlayerMapStatsByDbIdAsync(long   steamIdValue,
                                                                                      string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return (0f, 0);
        }

        var stats = await _db.Queryable<PlayerMapStatsEntity>()
                             .Where(x => x.SteamId == steamIdValue && x.MapId == mapId.Value)
                             .FirstAsync(OperationCancellation);

        if (stats is null)
        {
            return (0f, 0);
        }

        return (stats.PlayTime, stats.PlayCount);
    }
}
