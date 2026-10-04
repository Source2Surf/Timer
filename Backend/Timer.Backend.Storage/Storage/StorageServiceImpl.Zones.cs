using System.Collections.Generic;
using System.Threading.Tasks;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Models.Zone;
using SqlSugar;

namespace Timer.Backend.Storage;

internal sealed partial class StorageServiceImpl
{
    public async Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName)
    {
        var mapId = await ResolveMapIdByNameAsync(mapName);

        if (mapId is null)
        {
            return [];
        }

        var entities = await _db.Queryable<ZoneEntity>()
                                .Where(z => z.MapId == mapId.Value)
                                .ToListAsync(OperationCancellation);

        var result = new List<ZoneData>(entities.Count);

        foreach (var entity in entities)
        {
            result.Add(ZoneEntityMapper.ToData(entity));
        }

        return result;
    }

    public async Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones)
    {
        var mapId = await EnsureMapIdByNameAsync(mapName);
        await WithRecordTransactionAsync(async () =>
        {
            // Serialize whole snapshots, including an initially empty zone range.
            await LockMapAsync(mapId);
            await _db.Deleteable<ZoneEntity>()
                     .Where(z => z.MapId == mapId)
                     .ExecuteCommandAsync(OperationCancellation);

            if (zones.Count > 0)
            {
                // Recreate entities on each retry so rolled-back identity values are not reused.
                var entities = new List<ZoneEntity>(zones.Count);
                foreach (var zone in zones)
                    entities.Add(ZoneEntityMapper.ToEntity(zone, mapId));
                await _db.Insertable(entities).ExecuteCommandAsync(OperationCancellation);
            }
        });
    }
}
