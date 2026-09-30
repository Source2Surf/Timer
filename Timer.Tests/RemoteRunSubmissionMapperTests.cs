using Microsoft.Extensions.Configuration;
using Sharp.Shared.Types;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;
using Xunit;

namespace Timer.Tests;

public sealed class RemoteRunSubmissionMapperTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(5_000_000u)]
    [InlineData(5_529_600u)]
    public void TickBasedTimesRoundTripExactlyWithinBackendLimit(uint timerTicks)
    {
        var record = CreateRecord();
        record.Time = timerTicks * TimerConstants.TickInterval;
        record.Checkpoints.Clear();

        var request = RemoteRunSubmissionMapper.CreateMain(76561198000000001UL,
                                                            "surf_tick_time",
                                                            record,
                                                            DateTime.UtcNow,
                                                            GetRemoteOptions());
        Assert.Equal((long)timerTicks * 15_625, request.TimeMicros);
        Assert.Equal(record.Time, (float)(request.TimeMicros / 1_000_000d));
    }

    [Fact]
    public void FinishFactsPreserveMainMotionWithoutPluginScorePolicy()
    {
        var timer = new TestTimerInfo();
        var record = RecordSaver.CreateRecordRequest(timer);
        var request = RemoteRunSubmissionMapper.CreateMain(76561198000000001UL,
                                                            "surf_motion",
                                                            record,
                                                            DateTime.UtcNow,
                                                            GetRemoteOptions());

        Assert.Equal(101f, request.Motion.StartX);
        Assert.Equal(202f, request.Motion.AverageY);
        Assert.Equal(303f, request.Motion.MaxZ);
        Assert.Equal(404f, request.Motion.EndX);
        Assert.Equal(1.0, record.StyleFactor);
    }

    [Fact]
    public void RemoteModeUsesRulesetOneByDefault()
    {
        var minimalConfiguration = BuildConfiguration(("Timer:ScoreWrite:Mode", "remote-write"));
        var options = RemoteRunSubmissionOptions.FromConfiguration(
            minimalConfiguration,
            ScoreWriteModeOptions.FromConfiguration(minimalConfiguration));

        Assert.Equal(1, options.RulesetVersion);
    }

    [Fact]
    public void ExplicitInvalidRulesetStillFails()
    {
        var configuration = BuildConfiguration(("Timer:ScoreWrite:Mode", "remote-write"),
                                               ("Timer:ScoreWrite:RulesetVersion", "0"));
        Assert.Throws<InvalidOperationException>(() => RemoteRunSubmissionOptions.FromConfiguration(
                                                       configuration,
                                                       ScoreWriteModeOptions.FromConfiguration(configuration)));
    }

    [Theory]
    [InlineData("StyleFactor")]
    [InlineData("Points")]
    public void RemoteModeRejectsClientScorePolicySettings(string setting)
    {
        var configuration = BuildConfiguration(("Timer:ScoreWrite:Mode", "remote-write"),
                                               ("Timer:ScoreWrite:RulesetVersion", "1"),
                                               ($"Timer:ScoreWrite:{setting}", "99"));

        Assert.Throws<InvalidOperationException>(() => RemoteRunSubmissionOptions.FromConfiguration(
                                                       configuration,
                                                       ScoreWriteModeOptions.FromConfiguration(configuration)));
    }

    [Fact]
    public void RemoteModeRejectsRemovedPluginVersionSetting()
    {
        var configuration = BuildConfiguration(("Timer:ScoreWrite:Mode", "remote-write"),
                                               ("Timer:ScoreWrite:PluginVersion", "obsolete"));

        Assert.Throws<InvalidOperationException>(() => RemoteRunSubmissionOptions.FromConfiguration(
                                                       configuration,
                                                       ScoreWriteModeOptions.FromConfiguration(configuration)));
    }

    [Fact]
    public void MapperCarriesFactsButNeverClientScorePolicy()
    {
        var options = GetRemoteOptions();
        var record = CreateRecord();
        record.StyleFactor = 99.0;
        var finishedAtUtc = new DateTime(2026, 9, 15, 12, 34, 56, 789, DateTimeKind.Utc);

        var request = RemoteRunSubmissionMapper.CreateMain(76561198000000001UL,
                                                            "surf_remote_mapper",
                                                            record,
                                                            finishedAtUtc,
                                                            options);

        Assert.NotEqual(Guid.Empty, request.SubmissionId);
        Assert.Equal(76561198000000001L, request.SteamId);
        Assert.Equal("surf_remote_mapper", request.MapName);
        Assert.Equal(RunKind.Main, request.RunKind);
        Assert.Equal(0, request.Stage);
        Assert.Equal(12_345_678L, request.TimeMicros);
        Assert.Equal(17, request.Jumps);
        Assert.Equal(31, request.Strafes);
        Assert.Equal(87.5f, request.Sync);
        Assert.Equal(1, request.ContractVersion);
        Assert.Equal(7, request.RulesetVersion);
        Assert.Equal(new DateTimeOffset(finishedAtUtc).ToUnixTimeMilliseconds(), request.FinishedAtUnixTimeMilliseconds);

        Assert.Equal(2, request.Checkpoints.Length);
        Assert.Equal((uint)1, request.Checkpoints[0].Index);
        Assert.Equal(4_000_000L, request.Checkpoints[0].TimeMicros);
        Assert.Equal(82f, request.Checkpoints[0].Sync);
        Assert.Equal(100f, request.Motion.StartX);
        Assert.Equal(200f, request.Motion.AverageY);
        Assert.Equal(300f, request.Motion.MaxZ);
        Assert.Null(typeof(SubmitRunRequest).GetProperty("StyleFactor"));
        Assert.Null(typeof(SubmitRunRequest).GetProperty("Points"));
        Assert.Null(typeof(SubmitRunRequest).GetProperty("PluginVersion"));
    }

    [Fact]
    public void MapperRejectsInvalidCheckpointOrderAndNonUtcFinishTime()
    {
        var record = CreateRecord();
        record.Checkpoints[1].CheckpointIndex = 1;

        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteRunSubmissionMapper.CreateMain(
                                                       76561198000000001UL,
                                                       "surf_remote_mapper",
                                                       record,
                                                       DateTime.UtcNow,
                                                       GetRemoteOptions()));

        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteRunSubmissionMapper.CreateMain(
                                                       76561198000000001UL,
                                                       "surf_remote_mapper",
                                                       CreateRecord(),
                                                       DateTime.Now,
                                                       GetRemoteOptions()));
    }

    [Fact]
    public async Task WriterEnqueuesBeforeItWaitsForCanonicalAcknowledgement()
    {
        var request = RemoteRunSubmissionMapper.CreateMain(76561198000000001UL,
                                                            "surf_remote_writer",
                                                            CreateRecord(),
                                                            DateTime.UtcNow,
                                                            GetRemoteOptions());
        SubmitRunRequest? observedRequest = null;
        var completion = new TaskCompletionSource<SubmitRunResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new RemoteRunSubmissionWriter((submitted, _) =>
        {
            observedRequest = submitted;
            return completion.Task;
        });

        var waiting = writer.EnqueueAndWaitAsync(request, CancellationToken.None);

        Assert.Same(request, observedRequest);
        Assert.False(waiting.IsCompleted);

        completion.SetResult(Canonical(request.SubmissionId));
        var acknowledgement = await waiting;

        Assert.Equal(SubmissionDisposition.Accepted, acknowledgement.Disposition);
        Assert.True(RemoteRunSubmissionMapper.IsCanonicalAcknowledgement(request, acknowledgement));

        acknowledgement.RankState = (RankState)255;
        Assert.False(RemoteRunSubmissionMapper.IsCanonicalAcknowledgement(request, acknowledgement));
    }

    [Fact]
    public async Task WriterPreservesQueueCapacityAndPermanentRejectionOutcomes()
    {
        var request = RemoteRunSubmissionMapper.CreateMain(76561198000000001UL,
                                                            "surf_remote_failures",
                                                            CreateRecord(),
                                                            DateTime.UtcNow,
                                                            GetRemoteOptions());
        var fullQueue = new RemoteRunSubmissionWriter((_, _) => Task.FromException<SubmitRunResponse>(
            new RunSubmissionEnqueueException(request.SubmissionId,
                                              SubmissionSpoolEnqueueDisposition.CapacityExceeded)));

        var queueException = await Assert.ThrowsAsync<RunSubmissionEnqueueException>(
            () => fullQueue.EnqueueAndWaitAsync(request, CancellationToken.None));
        Assert.Equal(SubmissionSpoolEnqueueDisposition.CapacityExceeded, queueException.Disposition);

        var rejected = new RemoteRunSubmissionWriter((_, _) => Task.FromException<SubmitRunResponse>(
            new RunSubmissionRejectedException(request.SubmissionId, "backend rejected request")));

        await Assert.ThrowsAsync<RunSubmissionRejectedException>(
            () => rejected.EnqueueAndWaitAsync(request, CancellationToken.None));
    }

    [Fact]
    public void AcknowledgedProjectionRequiresAStableMapIdentity()
    {
        var finishedAtUtc = new DateTime(2026, 9, 15, 12, 34, 56, 789, DateTimeKind.Utc);
        var request = RemoteRunSubmissionMapper.CreateStage(76561198000000001UL,
                                                             "surf_remote_projection",
                                                             CreateStageRecord(),
                                                             finishedAtUtc,
                                                             GetRemoteOptions());
        var response = Canonical(request.SubmissionId, AttemptResult.NewServerRecord, rank: 1, RankState.Ready);

        Assert.Throws<InvalidOperationException>(() => RemoteRunSubmissionMapper.ToAcknowledgedRun(request,
                                                                                                       response,
                                                                                                       "Player",
                                                                                                       mapId: 0));

        var projection = RemoteRunSubmissionMapper.ToAcknowledgedRun(request, response, "Player", mapId: 42);

        Assert.Equal(EAttemptResult.NewServerRecord, projection.RecordType);
        Assert.Equal(42UL, projection.SavedRecord.MapId);
        Assert.Equal(123L, projection.SavedRecord.Id);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(request.FinishedAtUnixTimeMilliseconds).UtcDateTime,
                     projection.SavedRecord.RunDate);
        Assert.InRange(projection.SavedRecord.Time, 12.34567f, 12.34569f);
        Assert.Equal(1, projection.Rank);
    }

    private static RemoteRunSubmissionOptions GetRemoteOptions()
    {
        var configuration = BuildConfiguration(("Timer:ScoreWrite:Mode", "remote-write"),
                                               ("Timer:ScoreWrite:RulesetVersion", "7"));
        return RemoteRunSubmissionOptions.FromConfiguration(configuration,
                                                             ScoreWriteModeOptions.FromConfiguration(configuration));
    }

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings)
        {
            values.Add(key, value);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static RecordRequest CreateRecord()
    {
        var record = new RecordRequest
        {
            Style = 4,
            Track = 2,
            Stage = 0,
            Time = 12.345678f,
            Jumps = 17,
            Strafes = 31,
            Sync = 87.5f,
            VelocityStartX = 100f,
            VelocityAvgY = 200f,
            VelocityMaxZ = 300f,
            VelocityEndX = 400f,
        };
        record.Checkpoints.Add(new RecordRequest.CheckpointRecord
        {
            CheckpointIndex = 1,
            Time = 4f,
            Sync = 82f,
        });
        record.Checkpoints.Add(new RecordRequest.CheckpointRecord
        {
            CheckpointIndex = 2,
            Time = 8f,
            Sync = 85f,
        });
        return record;
    }

    private static RecordRequest CreateStageRecord()
    {
        var record = CreateRecord();
        record.Stage = 2;
        return record;
    }

    private static SubmitRunResponse Canonical(Guid submissionId,
                                                AttemptResult attemptResult = AttemptResult.NewPersonalRecord,
                                                int rank = 0,
                                                RankState rankState = RankState.Pending)
        => new()
        {
            SubmissionId = submissionId,
            RunId = 123,
            AttemptResult = attemptResult,
            Rank = rank,
            ReceivedAtUnixTimeMilliseconds = 1_789_000_000_000,
            Disposition = SubmissionDisposition.Accepted,
            RankState = rankState,
        };

    private sealed class TestTimerInfo : ITimerInfo
    {
        public ETimerStatus Status => ETimerStatus.Running;
        public int Jumps => 2;
        public int Strafes => 3;
        public float Time => 12.5f;
        public Vector StartVelocity => new(101f, 0f, 0f);
        public Vector AvgVelocity => new(0f, 202f, 0f);
        public Vector EndVelocity => new(404f, 0f, 0f);
        public Vector MaxVelocity => new(0f, 0f, 303f);
        public float Sync => 0.8f;
        public EZoneType InZone => EZoneType.End;
        public int Track => 0;
        public int Style => 0;
        public int Checkpoint => 0;
        public IReadOnlyList<CheckpointInfo> Checkpoints => [];

        public void ChangeStyle(int style) => throw new NotSupportedException();
        public void ChangeTrack(int track) => throw new NotSupportedException();
    }
}
