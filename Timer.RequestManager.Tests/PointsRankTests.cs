using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class PointsRankTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-points-rank-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;

    public PointsRankTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance, enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
    }

    [Fact]
    public async Task RankCountsPlayersStrictlyAheadAndSharesTies()
    {
        await AddPlayerAsync(1, 100);
        await AddPlayerAsync(2, 50);
        await AddPlayerAsync(3, 50);
        await AddPlayerAsync(4, 0);

        Assert.Equal((1, 3), await _storage.GetPlayerPointsRankForReadApiAsync(Id(1)));
        Assert.Equal((2, 3), await _storage.GetPlayerPointsRankForReadApiAsync(Id(2)));
        Assert.Equal((2, 3), await _storage.GetPlayerPointsRankForReadApiAsync(Id(3)));
        Assert.Equal((0, 0), await _storage.GetPlayerPointsRankForReadApiAsync(Id(4)));
    }

    [Fact]
    public async Task PlayerDroppingToZeroBetweenReadsIsReportedAsUnranked()
    {
        await AddPlayerAsync(1, 100);
        await AddPlayerAsync(2, 50);

        // Commit a concurrent change on another connection after the player's points were read
        // but before the ranking aggregate runs. The rank must never exceed the total.
        var changed = false;
        _storage.Db.Aop.OnLogExecuting = (sql, _) =>
        {
            if (changed || !sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)) return;
            changed = true;
            using var other = new SqlSugarClient(new ConnectionConfig
            {
                DbType = DbType.Sqlite,
                ConnectionString = $"Data Source={_path};Pooling=False",
                IsAutoCloseConnection = true,
            });
            other.Updateable<PlayerEntity>()
                 .SetColumns(p => p.Points == 0u)
                 .Where(p => p.SteamId == unchecked((long)Id(2)))
                 .ExecuteCommand();
        };

        var (rank, total) = await _storage.GetPlayerPointsRankForReadApiAsync(Id(2));

        Assert.True(changed);
        Assert.Equal((0, 0), (rank, total));
    }

    private static ulong Id(int player) => 76561198000000000UL + (ulong)player;

    private async Task AddPlayerAsync(int player, uint points)
    {
        await _storage.GetPlayerProfile(new SteamID(Id(player)), $"player {player}");
        await _storage.Db.Updateable<PlayerEntity>()
                      .SetColumns(p => p.Points == points)
                      .Where(p => p.SteamId == unchecked((long)Id(player)))
                      .ExecuteCommandAsync();
    }

    public void Dispose()
    {
        _storage.Shutdown();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
