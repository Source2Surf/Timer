using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<byte[]?> GetPlayerSettingsAsync(SteamID steamId)
    {
        var steamIdValue = ToDbSteamId(steamId);

        var rows = await _db.Queryable<PlayerSettingsEntity>()
                            .Where(x => x.SteamId == steamIdValue)
                            .Select(x => x.Data)
                            .ToListAsync(OperationCancellation);

        return rows.Count > 0 ? rows[0] : null;
    }

    // Empty data means all defaults, which needs no row.
    public async Task SavePlayerSettingsAsync(SteamID steamId, byte[] data)
    {
        var steamIdValue = ToDbSteamId(steamId);

        if (data.Length == 0)
        {
            await _db.Deleteable<PlayerSettingsEntity>()
                     .Where(x => x.SteamId == steamIdValue)
                     .ExecuteCommandAsync(OperationCancellation);

            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (await UpdatePlayerSettingsRowAsync(steamIdValue, data, now) == 1)
        {
            return;
        }

        try
        {
            await _db.Insertable(new PlayerSettingsEntity { SteamId = steamIdValue, Data = data, UpdatedAtUnixMilliseconds = now })
                     .ExecuteCommandAsync(OperationCancellation);
        }
        catch (Exception ex) when (IsUniqueKeyViolation(ex))
        {
            _logger.LogDebug(ex, "PlayerSettings insert raced for {steamId}, retrying update.", steamId);

            if (await UpdatePlayerSettingsRowAsync(steamIdValue, data, now) != 1)
            {
                throw new InvalidOperationException("Player settings could not be saved.");
            }
        }
    }

    private Task<int> UpdatePlayerSettingsRowAsync(long steamId, byte[] data, long now)
        => _db.Updateable<PlayerSettingsEntity>()
              .SetColumns(x => x.Data == data)
              .SetColumns(x => x.UpdatedAtUnixMilliseconds == now)
              .Where(x => x.SteamId == steamId)
              .ExecuteCommandAsync(OperationCancellation);
}
