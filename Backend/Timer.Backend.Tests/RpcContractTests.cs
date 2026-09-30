using System;
using MessagePack;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class RpcContractTests
{
    [Fact]
    public void SubmitRunRequestRoundTripsThroughMessagePack()
    {
        var submissionId = Guid.NewGuid();
        var request = new SubmitRunRequest
        {
            SubmissionId = submissionId,
            SteamId = 76561198000000001L,
            MapName = "surf_contract",
            RunKind = RunKind.Main,
            Style = 2,
            Track = 1,
            TimeMicros = 12_345_678,
            Jumps = 4,
            Strafes = 8,
            Sync = 91.5f,
            Motion = new MotionDto
            {
                StartX = 1,
                AverageY = 2,
                MaxZ = 3,
                EndX = 4,
            },
            Checkpoints =
            [
                new CheckpointDto
                {
                    Index = 1,
                    TimeMicros = 6_000_000,
                    Sync = 90,
                    Motion = new MotionDto { MaxX = 100 },
                },
            ],
            FinishedAtUnixTimeMilliseconds = 1_800_000_000_000,
            ContractVersion = 1,
            RulesetVersion = 7,
        };

        var payload = MessagePackSerializer.Serialize(request);
        var copy = MessagePackSerializer.Deserialize<SubmitRunRequest>(payload);

        Assert.Equal(submissionId, copy.SubmissionId);
        Assert.Equal(request.SteamId, copy.SteamId);
        Assert.Equal(request.MapName, copy.MapName);
        Assert.Equal(request.RunKind, copy.RunKind);
        Assert.Equal(request.TimeMicros, copy.TimeMicros);
        Assert.Equal(request.Motion.MaxZ, copy.Motion.MaxZ);
        Assert.Single(copy.Checkpoints);
        Assert.Equal(request.Checkpoints[0].TimeMicros, copy.Checkpoints[0].TimeMicros);
        Assert.Equal(request.ContractVersion, copy.ContractVersion);
        Assert.Equal(request.RulesetVersion, copy.RulesetVersion);
    }

    [Fact]
    public void SubmissionResponseRoundTripsWithRankState()
    {
        var response = new GetSubmissionStatusResponse
        {
            Found = true,
            Submission = new SubmitRunResponse
            {
                SubmissionId = Guid.NewGuid(),
                RunId = ulong.MaxValue,
                AttemptResult = AttemptResult.NewPersonalRecord,
                Rank = 0,
                RankState = RankState.Pending,
                ReceivedAtUnixTimeMilliseconds = 1_800_000_000_000,
                Disposition = SubmissionDisposition.AlreadyApplied,
            },
        };

        var payload = MessagePackSerializer.Serialize(response);
        var copy = MessagePackSerializer.Deserialize<GetSubmissionStatusResponse>(payload);

        Assert.True(copy.Found);
        Assert.NotNull(copy.Submission);
        Assert.Equal(response.Submission.RunId, copy.Submission.RunId);
        Assert.Equal(RankState.Pending, copy.Submission.RankState);
        Assert.Equal(SubmissionDisposition.AlreadyApplied, copy.Submission.Disposition);
    }

    [Fact]
    public void EnsurePlayerProfileContractsRoundTripThroughMessagePack()
    {
        var request = new EnsurePlayerProfileRequest
        {
            SteamId = 76561198000000001L,
            Name = "Profile Player",
        };
        var response = new EnsurePlayerProfileResponse
        {
            PlayerId = 42,
            SteamId = request.SteamId,
            Name = request.Name,
            Points = 123,
            JoinDateUnixTimeMilliseconds = 1_725_000_000_000,
            LastSeenDateUnixTimeMilliseconds = 1_725_000_001_000,
        };

        var requestCopy = MessagePackSerializer.Deserialize<EnsurePlayerProfileRequest>(
            MessagePackSerializer.Serialize(request));
        var responseCopy = MessagePackSerializer.Deserialize<EnsurePlayerProfileResponse>(
            MessagePackSerializer.Serialize(response));

        Assert.Equal(request.SteamId, requestCopy.SteamId);
        Assert.Equal(request.Name, requestCopy.Name);
        Assert.Equal(response.PlayerId, responseCopy.PlayerId);
        Assert.Equal(response.SteamId, responseCopy.SteamId);
        Assert.Equal(response.Name, responseCopy.Name);
        Assert.Equal(response.Points, responseCopy.Points);
        Assert.Equal(response.JoinDateUnixTimeMilliseconds, responseCopy.JoinDateUnixTimeMilliseconds);
        Assert.Equal(response.LastSeenDateUnixTimeMilliseconds, responseCopy.LastSeenDateUnixTimeMilliseconds);
    }
}
