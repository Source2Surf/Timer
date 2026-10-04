using System.Data.Common;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Storage.Tests;

/// <summary>
/// Provider acceptance for the signed-BIGINT total-score preflight. SQLite's
/// arithmetic is not enough here: each opt-in case runs the configured provider's
/// production translation before a uint player total is written.
/// </summary>
[Collection(SqlSchemaMutationCollection.Name)]
public sealed class ScoreRangeProviderTests
{
    private const string MySqlConnectionEnvironment = "TIMER_TEST_MYSQL";
    private const string PostgreSqlConnectionEnvironment = "TIMER_TEST_POSTGRES";

    [ScoreRangeProviderFact(MySqlConnectionEnvironment)]
    public Task SingleBoardUintOverflowRollsBackOnMySql()
        => SingleBoardUintOverflowRollsBackAsync(DbType.MySql, MySqlConnectionEnvironment, "mysql");

    [ScoreRangeProviderFact(PostgreSqlConnectionEnvironment)]
    public Task SingleBoardUintOverflowRollsBackOnPostgreSql()
        => SingleBoardUintOverflowRollsBackAsync(DbType.PostgreSQL, PostgreSqlConnectionEnvironment, "postgresql");

    [ScoreRangeProviderFact(MySqlConnectionEnvironment)]
    public Task TotalUintOverflowCapsThePlayerTotalOnMySql()
        => TotalUintOverflowCapsThePlayerTotalAsync(DbType.MySql, MySqlConnectionEnvironment, "mysql");

    [ScoreRangeProviderFact(PostgreSqlConnectionEnvironment)]
    public Task TotalUintOverflowCapsThePlayerTotalOnPostgreSql()
        => TotalUintOverflowCapsThePlayerTotalAsync(DbType.PostgreSQL, PostgreSqlConnectionEnvironment, "postgresql");

    private static Task SingleBoardUintOverflowRollsBackAsync(
        DbType databaseType,
        string connectionEnvironment,
        string providerToken)
        => WithStoreAsync(databaseType, connectionEnvironment, async storage =>
        {
            var player = NewPlayer();
            var map = await storage.GetMapInfo($"surf_{providerToken}_score_overflow_{Guid.NewGuid():N}");
            await SetTierAsync(storage, map.MapId, 27);
            await AddRunAsync(storage, map.MapId, player, style: 0);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1));

            Assert.Equal(0, await storage.Db.Queryable<PlayerTrackScoreEntity>()
                                        .Where(score => score.MapId == map.MapId)
                                        .CountAsync());
            Assert.Equal(0u, await storage.Db.Queryable<PlayerEntity>()
                                             .Where(row => row.SteamId == unchecked((long)player.AsPrimitive()))
                                             .Select(row => row.Points)
                                             .SingleAsync());
        });

    private static Task TotalUintOverflowCapsThePlayerTotalAsync(
        DbType databaseType,
        string connectionEnvironment,
        string providerToken)
        => WithStoreAsync(databaseType, connectionEnvironment, async storage =>
        {
            var player = NewPlayer();
            var map = await storage.GetMapInfo($"surf_{providerToken}_total_overflow_{Guid.NewGuid():N}");
            await SetTierAsync(storage, map.MapId, 26);
            await AddRunAsync(storage, map.MapId, player, style: 0);
            await AddRunAsync(storage, map.MapId, player, style: 1);

            await storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
            var first = await storage.Db.Queryable<PlayerTrackScoreEntity>()
                                     .Where(score => score.MapId == map.MapId && score.Style == 0)
                                     .SingleAsync();
            Assert.InRange(first.Points, 1u, uint.MaxValue);
            Assert.True(first.Points > int.MaxValue,
                        "The physical Points column must retain a board score above signed INT range.");

            // The second board's own score fits, but the player's cross-board total does not.
            // The board must still commit (so other players on it keep updating); only this
            // player's total is capped instead of the whole recalculation rolling back.
            await storage.RecalculateTrackScoresAsync(map.MapId, 1, 0, 1);

            var persisted = await storage.Db.Queryable<PlayerTrackScoreEntity>()
                                       .Where(score => score.MapId == map.MapId)
                                       .ToListAsync();
            Assert.Equal(2, persisted.Count);
            Assert.True((ulong)persisted[0].Points + persisted[1].Points > uint.MaxValue);
            Assert.Equal(uint.MaxValue, await storage.Db.Queryable<PlayerEntity>()
                                                        .Where(row => row.SteamId == unchecked((long)player.AsPrimitive()))
                                                        .Select(row => row.Points)
                                                        .SingleAsync());
        });

    private static async Task WithStoreAsync(
        DbType databaseType,
        string connectionEnvironment,
        Func<StorageServiceImpl, Task> action)
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionEnvironment)!;
        EnsureDisposableLoopbackDatabase(connectionString, databaseType);
        var storage = new StorageServiceImpl(databaseType, connectionString,
                                             NullLogger<StorageServiceImpl>.Instance,
                                             enableScoreRecalcWorker: false);
        try
        {
            storage.Init();
            await action(storage);
        }
        finally
        {
            storage.Shutdown();
        }
    }

    private static async Task SetTierAsync(StorageServiceImpl storage, ulong mapId, byte tier)
        => _ = await storage.Db.Updateable<MapEntity>()
                            .SetColumns(row => row.Tier == tier)
                            .Where(row => row.MapId == mapId)
                            .ExecuteCommandAsync();

    private static async Task AddRunAsync(StorageServiceImpl storage, ulong mapId, SteamID player, int style)
    {
        await storage.GetPlayerProfile(player, "Provider score range player");
        await storage.Db.Insertable(new RunEntity
        {
            MapId = mapId,
            SteamId = unchecked((long)player.AsPrimitive()),
            RunType = RunType.Main,
            Style = style,
            Time = 80,
            DateUnixTimeMilliseconds = StorageServiceImpl.ToUnixTimeMilliseconds(DateTime.UtcNow),
        }).ExecuteCommandAsync();
    }

    private static SteamID NewPlayer()
        => new(76561198000000000UL + (ulong)Random.Shared.NextInt64(1, 1_000_000_000));

    private static void EnsureDisposableLoopbackDatabase(string connectionString, DbType databaseType)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var server = ReadConnectionValue(builder, "Server", "Host");
        var database = ReadConnectionValue(builder, "Database", "Initial Catalog");
        var defaultPort = databaseType == DbType.MySql ? 3306 : 5432;
        var port = int.TryParse(ReadConnectionValue(builder, "Port"), out var parsedPort) ? parsedPort : defaultPort;
        var isLoopback = string.Equals(server, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(server, "::1", StringComparison.OrdinalIgnoreCase);
        if (!isLoopback || port < 10240 || port > 65535
            || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Score range integration tests require a disposable loopback {databaseType} test database on a high port with 'test' in its database name.");
        }
    }

    private static string ReadConnectionValue(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private sealed class ScoreRangeProviderFactAttribute : FactAttribute
    {
        public ScoreRangeProviderFactAttribute(string connectionEnvironment)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(connectionEnvironment)))
            {
                Skip = $"Set {connectionEnvironment} to a disposable loopback provider test database.";
            }
        }
    }
}
