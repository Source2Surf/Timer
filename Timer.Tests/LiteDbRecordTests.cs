using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Xunit;

namespace Timer.Tests;

public sealed class LiteDbRecordTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-records-{Guid.NewGuid():N}.db");
    private readonly RequestManagerLiteDB _storage;

    public LiteDbRecordTests()
    {
        _storage = new RequestManagerLiteDB(_path, NullLogger<RequestManagerLiteDB>.Instance);
        _storage.Init();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task ClassifiesFirstFinishImprovementTieAndSlowerAttempt(int stage)
    {
        var first = new SteamID(76561198000000001);
        var second = new SteamID(76561198000000002);
        Task<(EAttemptResult result, RunRecord record, int rank)> Finish(SteamID player, float time)
        {
            var request = new RecordRequest { Time = time, Stage = stage };
            return stage == 0 ? _storage.AddPlayerRecord(player, "surf_test", request)
                              : _storage.AddPlayerStageRecord(player, "surf_test", request);
        }

        var wr = await Finish(first, 80);
        Assert.Equal(EAttemptResult.NewServerRecord, wr.result);
        Assert.Equal(1, wr.rank);
        Assert.Equal(EAttemptResult.NewPersonalRecord, (await Finish(second, 120)).result);
        Assert.Equal(EAttemptResult.NewPersonalRecord, (await Finish(second, 100)).result);
        Assert.Equal(EAttemptResult.NoNewRecord, (await Finish(second, 100)).result);
        Assert.Equal(EAttemptResult.NoNewRecord, (await Finish(second, 140)).result);
        Assert.Equal(EAttemptResult.NewServerRecord, (await Finish(second, 70)).result);
    }

    public void Dispose()
    {
        _storage.Dispose();
        File.Delete(_path);
    }

    [Fact]
    public async Task MainAndStageCheckpointsUseDistinctRunIds()
    {
        var player = new SteamID(76561198000000001);
        var main = new RecordRequest { Time = 80 };
        main.Checkpoints.Add(new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 40 });
        var stage = new RecordRequest { Time = 20, Stage = 1 };
        stage.Checkpoints.Add(new RecordRequest.CheckpointRecord { CheckpointIndex = 1, Time = 10 });
        var (_, mainRun, _) = await _storage.AddPlayerRecord(player, "surf_probe", main);
        var (_, stageRun, _) = await _storage.AddPlayerStageRecord(player, "surf_probe", stage);
        var checkpoints = await _storage.GetRecordCheckpoints(mainRun.Id);
        Assert.NotEqual(mainRun.Id, stageRun.Id);
        Assert.Equal(40, Assert.Single(checkpoints).Time);
        Assert.Equal(10, Assert.Single(await _storage.GetRecordCheckpoints(stageRun.Id)).Time);

        _storage.Shutdown();
        _storage.Init();
        var (_, nextRun, _) = await _storage.AddPlayerStageRecord(player, "surf_probe", stage);
        Assert.True(nextRun.Id > Math.Max(mainRun.Id, stageRun.Id));
        Assert.Equal(40, Assert.Single(await _storage.GetRecordCheckpoints(mainRun.Id)).Time);
    }
}
