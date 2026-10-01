using System;
using System.Collections.Generic;
using Sharp.Shared.Units;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using SqlSugar;

namespace Timer.RequestManager.Storage;

internal sealed partial class StorageServiceImpl
{
    private static long ToDbSteamId(SteamID steamId) => unchecked((long)steamId.AsPrimitive());

    private static bool IsUniqueKeyViolation(Exception exception)
        => exception is MySqlConnector.MySqlException { Number: 1062 }
            or Npgsql.PostgresException { SqlState: "23505" }
           || (exception.InnerException is { } inner && IsUniqueKeyViolation(inner));

    private async Task<MapEntity?> FindMapByNameAsync(string mapName)
    {
        var map = await _db.Queryable<MapEntity>()
                           .Where(x => x.File == mapName)
                           .FirstAsync(OperationCancellation);

        // MySQL's default utf8mb4_0900_ai_ci collation compares case- and accent-insensitively, so
        // the query can return a different map (surf_edge -> surf_édge). Map names are exact keys.
        return map is not null && string.Equals(map.File, mapName, StringComparison.Ordinal) ? map : null;
    }

    internal async Task<ulong?> ResolveMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (_mapIdCache.TryGetValue(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        var mapEntity = await FindMapByNameAsync(mapKey);

        if (mapEntity is null)
        {
            return null;
        }

        _mapIdCache[mapKey] = mapEntity.MapId;

        return mapEntity.MapId;
    }

    internal async Task<ulong> EnsureMapIdByNameAsync(string mapName)
    {
        var mapKey = ToMapKey(mapName);

        if (_mapIdCache.TryGetValue(mapKey, out var cachedMapId))
        {
            return cachedMapId;
        }

        return (await EnsureMapEntityByKeyAsync(mapKey, mapName)).MapId;
    }

    private async Task<MapEntity> EnsureMapEntityByKeyAsync(string mapKey, string mapName)
    {
        var mapEntity = await FindMapByNameAsync(mapKey);

        if (mapEntity is null)
        {
            mapEntity = new ()
            {
                File   = mapKey,
                Tier   = 1,
                Stages = 0,
            };

            try
            {
                var newId = await _db.Insertable(mapEntity).ExecuteReturnBigIdentityAsync(OperationCancellation);
                mapEntity.MapId = unchecked((ulong) newId);
            }
            catch (Exception ex)
            {
                // Usually a duplicate-key race with another server inserting the same map —
                // the re-read below picks up the winning row. If the re-read finds nothing,
                // the insert genuinely failed: surface it instead of returning (and caching)
                // an unsaved entity with MapId 0.
                if (await FindMapByNameAsync(mapKey) is not { } raced)
                {
                    var collidingName = await _db.Queryable<MapEntity>()
                                                 .Where(x => x.File == mapKey)
                                                 .Select(x => x.File)
                                                 .FirstAsync(OperationCancellation);
                    if (collidingName is not null)
                    {
                        throw new InvalidOperationException(
                            $"Map '{mapKey}' cannot be created: the database collation treats it as equal to existing map '{collidingName}' (MySQL compares names case- and accent-insensitively). Rename one of them.",
                            ex);
                    }

                    _logger.LogError(ex, "Failed to insert map row for {map}", mapName);

                    throw;
                }

                _logger.LogDebug(ex, "Map insert raced for map {map}; using the winning row.", mapName);
                mapEntity = raced;
            }
        }

        _mapIdCache[mapKey] = mapEntity.MapId;

        return mapEntity;
    }

    private async Task SyncMapTrackTiersAsync(ulong mapId, byte[]? tiers)
    {
        await _db.Deleteable<MapTrackEntity>()
                 .Where(x => x.MapId == mapId)
                 .ExecuteCommandAsync(OperationCancellation);

        if (tiers is null || tiers.Length <= 1)
        {
            return;
        }

        var entities = new List<MapTrackEntity>(tiers.Length - 1);

        for (var index = 1; index < Math.Min(tiers.Length, MapProfile.DefaultTrackCount); index++)
        {
            var tier = tiers[index];

            if (tier == 0)
            {
                continue;
            }

            entities.Add(new ()
            {
                MapId = mapId,
                Track = (ushort) index,
                Tier  = tier,
            });
        }

        if (entities.Count > 0)
        {
            await _db.Insertable(entities).ExecuteCommandAsync(OperationCancellation);
        }
    }

    private SqlSugarScope CreateClient(DbType dbType, string connectionString) =>
        new (new ConnectionConfig
        {
            DbType                = dbType,
            ConnectionString      = connectionString,
            IsAutoCloseConnection = true,
            InitKeyType           = InitKeyType.Attribute,
            // SqlSugar's own messages are bilingual by default. This setting is process-wide.
            LanguageType          = LanguageType.English,
            // A mixed-version or incomplete entity must never delete a production
            // column merely because CodeFirst does not know about it.
            ConfigureExternalServices = new ConfigureExternalServices
            {
                EntityNameService = (_, entity) => entity.IsDisabledDelete = true,
            },
        }, ConfigureSqlLogging);

    private static byte GetTier(byte[]? tiers, int track)
    {
        if (tiers is null || track < 0 || track >= tiers.Length || tiers[track] == 0)
        {
            return 1;
        }

        return tiers[track];
    }

    private static ushort ToUInt16(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort) value;
    }

    private static uint ToUInt32(int value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value == int.MaxValue)
        {
            return int.MaxValue;
        }

        return (uint) value;
    }

    private static int ToInt32(uint value)
    {
        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int) value;
    }

    private static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return 1;
        }

        return limit >= IRequestManager.DefaultRecordLimit ? IRequestManager.DefaultRecordLimit : limit;
    }

    private static string ToMapKey(string mapName)
        => mapName.ToLowerInvariant();

    public async Task<IReadOnlyList<string>> GetAllMapNamesAsync()
        => await _db.Queryable<MapEntity>()
                    .OrderBy(x => x.File)
                    .Select(x => x.File)
                    .ToListAsync(OperationCancellation);

    private sealed class AttemptBestTimesRow
    {
        // 0 when the player has no best row. The queries aggregate MAX over a CASE whose
        // other-player branch is 0 rather than NULL: SqlSugar's PostgreSQL provider sends a
        // null unsigned parameter as 0, so a NULL branch made every other player's row read
        // as id 0 and hid this player's row. The unique key allows one row per player, and
        // ids start at 1, so MAX is exactly that row.
        public ulong? PlayerBestRowId { get; set; }
        public ulong? PlayerBestRunId { get; set; }

        public float? ServerBestTime { get; set; }

        // SqlSugar's MySQL-family materializer maps a nullable float CASE aggregate
        // to zero when the projection targets a DTO. Keep this value double-typed
        // so an existing personal best remains distinguishable from no value.
        public double? PlayerBestTime { get; set; }
    }

}
