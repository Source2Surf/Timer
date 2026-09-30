using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Common.Entities;
using Source2Surf.Timer.Common.Enums;
using SqlSugar;
using Timer.RequestManager.Storage;
using Xunit;

namespace Timer.RequestManager.Tests;

public sealed class ScoreRangeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-score-range-{Guid.NewGuid():N}.db");
    private readonly StorageServiceImpl _storage;
    private static readonly SteamID Player = new(76561198000000001);

    public ScoreRangeTests()
    {
        _storage = new StorageServiceImpl(DbType.Sqlite, $"Data Source={_path};Pooling=False",
                                          NullLogger<StorageServiceImpl>.Instance,
                                          enableScoreRecalcWorker: false);
        _storage.Db.CurrentConnectionConfig.ConfigureExternalServices.EntityService = (_, column) =>
        {
            if (column.IsIdentity) column.DataType = "INTEGER";
        };
        _storage.Init();
    }

    [Fact]
    public async Task ScoreAboveUintRangeRollsBackTheWholeTrack()
    {
        var map = await _storage.GetMapInfo($"surf_score_overflow_{Guid.NewGuid():N}");
        await SetTierAsync(map.MapId, 27);
        await AddRunAsync(map.MapId, style: 0);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1));

        Assert.Equal(0, await _storage.Db.Queryable<PlayerTrackScoreEntity>().CountAsync());
        Assert.Equal(0u, (await _storage.Db.Queryable<PlayerEntity>().SingleAsync()).Points);
    }

    [Fact]
    public async Task SumOfIndividuallyValidBoardsCapsPlayerPointsWithoutBlockingTheBoard()
    {
        var map = await _storage.GetMapInfo($"surf_total_overflow_{Guid.NewGuid():N}");
        await SetTierAsync(map.MapId, 26);
        await AddRunAsync(map.MapId, style: 0);
        await AddRunAsync(map.MapId, style: 1);

        await _storage.RecalculateTrackScoresAsync(map.MapId, 0, 0, 1);
        var firstScore = await _storage.Db.Queryable<PlayerTrackScoreEntity>()
                                       .Where(x => x.Style == 0).SingleAsync();
        Assert.InRange(firstScore.Points, 1u, uint.MaxValue);

        // The second board's score fits on its own; only the player's cross-board total does not.
        // The board must commit (so other players on it keep updating) and the total is capped.
        await _storage.RecalculateTrackScoresAsync(map.MapId, 1, 0, 1);

        var persisted = await _storage.Db.Queryable<PlayerTrackScoreEntity>().ToListAsync();
        Assert.Equal(2, persisted.Count);
        Assert.True((ulong)persisted[0].Points + persisted[1].Points > uint.MaxValue);
        Assert.Equal(uint.MaxValue, (await _storage.Db.Queryable<PlayerEntity>().SingleAsync()).Points);
    }

    private async Task SetTierAsync(ulong mapId, byte tier)
    {
        await _storage.Db.Updateable<MapEntity>()
                      .SetColumns(x => x.Tier == tier)
                      .Where(x => x.MapId == mapId)
                      .ExecuteCommandAsync();
    }

    private async Task AddRunAsync(ulong mapId, int style)
    {
        await _storage.GetPlayerProfile(Player, "Score range player");
        await _storage.Db.Insertable(new RunEntity
        {
            MapId = mapId,
            SteamId = unchecked((long)Player.AsPrimitive()),
            RunType = RunType.Main,
            Style = style,
            Time = 80,
        }).ExecuteCommandAsync();
    }

    public void Dispose()
    {
        _storage.Shutdown();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
