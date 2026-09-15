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
        var steamIdValue = ToDbSteamId(steamId);
        if (deltaSeconds <= 0f)
        {
            return;
        }

        var mapId = await EnsureMapIdByNameAsync(mapName);

        var affected = await _db.Updateable<PlayerMapStatsEntity>()
                                .SetColumns(x => x.PlayTime == x.PlayTime + deltaSeconds)
                                .SetColumns(x => x.PlayCount == x.PlayCount + 1)
                                .Where(x => x.SteamId == steamIdValue && x.MapId == mapId)
                                .ExecuteCommandAsync();

        if (affected > 0)
        {
            return;
        }

        var entity = new PlayerMapStatsEntity
        {
            SteamId   = ToDbSteamId(steamId),
            MapId     = mapId,
            PlayTime  = deltaSeconds,
            PlayCount = 1,
        };

        try
        {
            await _db.Insertable(entity).ExecuteCommandAsync();
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            _logger.LogDebug(ex,
                             "PlayerMapStats insert raced for {steamId} map {mapId}, retrying update.",
                             steamId,
                             mapId);

            await _db.Updateable<PlayerMapStatsEntity>()
                     .SetColumns(x => x.PlayTime == x.PlayTime + deltaSeconds)
                     .SetColumns(x => x.PlayCount == x.PlayCount + 1)
                     .Where(x => x.SteamId == steamIdValue && x.MapId == mapId)
                     .ExecuteCommandAsync();
        }
    }

    public async Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName)
    {
        var steamIdValue = ToDbSteamId(steamId);
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return (0f, 0);
        }

        var stats = await _db.Queryable<PlayerMapStatsEntity>()
                             .Where(x => x.SteamId == steamIdValue && x.MapId == mapId.Value)
                             .FirstAsync();

        if (stats is null)
        {
            return (0f, 0);
        }

        return (stats.PlayTime, stats.PlayCount);
    }
}
