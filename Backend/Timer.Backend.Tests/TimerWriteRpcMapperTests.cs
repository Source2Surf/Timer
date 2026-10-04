using System;
using System.Collections.Generic;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Shared;
using Timer.Backend.Configuration;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteRpcMapperTests
{
    [Fact]
    public void MapsTrustedBackendRulesetAndStylePolicyInsteadOfDtoPolicy()
    {
        var options = CreateOptions();
        var request = CreateRequest();
        request.RulesetVersion = 7;
        request.Style = 0;

        var command = TimerWriteRpcMapper.ToCommand(request, options);

        Assert.Equal(7, command.RulesetVersion);
        Assert.Equal(1.5, command.StyleFactor);
        Assert.Equal(DateTimeKind.Utc, command.FinishedAtUtc.Kind);
        var checkpoint = Assert.Single(command.Checkpoints);
        Assert.Equal(1, checkpoint.CheckpointIndex);
    }

    [Fact]
    public void MapsPlayerProfileWithoutServerIdentity()
    {
        var command = TimerWriteRpcMapper.ToPlayerProfileCommand(new EnsurePlayerProfileRequest
        {
            SteamId = 76561198000000001L,
            Name = "Profile Player",
        });

        var response = TimerWriteRpcMapper.ToPlayerProfileResponse(new TimerBackendPlayerProfileResult
        {
            PlayerId = 42,
            SteamId = command.SteamId,
            Name = command.Name,
            Points = 123,
            JoinDateUtc = new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc),
            LastSeenDateUtc = new DateTime(2026, 9, 15, 1, 2, 4, DateTimeKind.Utc),
        });

        Assert.Equal(76561198000000001L, command.SteamId);
        Assert.Equal("Profile Player", command.Name);
        Assert.Equal(42, response.PlayerId);
        Assert.Equal(123U, response.Points);
        Assert.Equal(1_789_434_123_000, response.JoinDateUnixTimeMilliseconds);
        Assert.Equal(1_789_434_124_000, response.LastSeenDateUnixTimeMilliseconds);
    }

    [Theory]
    [InlineData(0, "Player")]
    [InlineData(-1, "Player")]
    [InlineData(76561198000000001L, "")]
    [InlineData(76561198000000001L, " Player")]
    [InlineData(76561198000000001L, "Player ")]
    [InlineData(76561198000000001L, "Player\nName")]
    public void InvalidPlayerProfileFieldsAreInvalidArgument(long steamId, string name)
    {
        var exception = Assert.Throws<RpcException>(() =>
            TimerWriteRpcMapper.ToPlayerProfileCommand(new EnsurePlayerProfileRequest
            {
                SteamId = steamId,
                Name = name,
            }));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public void PlayerNameLongerThanDatabaseBoundIsInvalidArgument()
    {
        var exception = Assert.Throws<RpcException>(() =>
            TimerWriteRpcMapper.ToPlayerProfileCommand(new EnsurePlayerProfileRequest
            {
                SteamId = 76561198000000001L,
                Name = new string('P', 193),
            }));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public void MismatchedRulesetAndDisabledStyleUseStablePolicyStatus()
    {
        var options = CreateOptions();
        var wrongRuleset = CreateRequest();
        wrongRuleset.RulesetVersion = 8;
        var disabledStyle = CreateRequest();
        disabledStyle.Style = 1;

        Assert.Equal(StatusCode.FailedPrecondition,
                     Assert.Throws<RpcException>(() => TimerWriteRpcMapper.ToCommand(wrongRuleset, options)).StatusCode);
        Assert.Equal(StatusCode.FailedPrecondition,
                     Assert.Throws<RpcException>(() => TimerWriteRpcMapper.ToCommand(disabledStyle, options)).StatusCode);
    }

    [Fact]
    public void ReplayAwareMappingRetainsObsoleteCanonicalRulesetButForbidsNewWrites()
    {
        var options = CreateOptions();
        var wrongRuleset = CreateRequest();
        wrongRuleset.RulesetVersion = 8;
        var disabledStyle = CreateRequest();
        disabledStyle.Style = 1;

        var (rulesetCommand, rulesetAccepted) = TimerWriteRpcMapper.ToReplayAwareCommand(wrongRuleset, options);
        var (styleCommand, styleAccepted) = TimerWriteRpcMapper.ToReplayAwareCommand(disabledStyle, options);
        Assert.Equal(8, rulesetCommand.RulesetVersion);
        Assert.Equal(1.5, rulesetCommand.StyleFactor);
        Assert.False(rulesetAccepted);
        Assert.Equal(1, styleCommand.Style);
        Assert.Equal(1, styleCommand.StyleFactor); // hash-only fallback; no new write is allowed
        Assert.False(styleAccepted);
    }

    [Fact]
    public void OutOfRangeTimestampAndCheckpointIndexAreInvalidArgument()
    {
        var options = CreateOptions();
        var timestamp = CreateRequest();
        timestamp.FinishedAtUnixTimeMilliseconds = long.MaxValue;
        var checkpointIndex = CreateRequest();
        checkpointIndex.Checkpoints[0].Index = uint.MaxValue;

        Assert.Equal(StatusCode.InvalidArgument,
                     Assert.Throws<RpcException>(() => TimerWriteRpcMapper.ToCommand(timestamp, options)).StatusCode);
        Assert.Equal(StatusCode.InvalidArgument,
                     Assert.Throws<RpcException>(() => TimerWriteRpcMapper.ToCommand(checkpointIndex, options)).StatusCode);
    }

    [Fact]
    public void OversizedCheckpointArrayIsRejectedBeforeDomainMapping()
    {
        var request = CreateRequest();
        request.Checkpoints = new CheckpointDto[TimerConstants.MAX_STAGE];

        var exception = Assert.Throws<RpcException>(() =>
            TimerWriteRpcMapper.ToCommand(request, CreateOptions()));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public void ResponseMappingPreservesStableRetryOutcome()
    {
        var response = TimerWriteRpcMapper.ToResponse(new TimerBackendRunSubmissionResult
        {
            SubmissionId = Guid.NewGuid(),
            RunId = 42,
            AttemptResult = TimerBackendAttemptResult.NewPersonalRecord,
            RankState = TimerBackendRankState.Pending,
            Rank = 0,
            Disposition = TimerBackendSubmissionDisposition.AlreadyApplied,
            ReceivedAtUtc = new DateTime(2026, 9, 15, 1, 2, 3, DateTimeKind.Utc),
        });

        Assert.Equal(SubmissionDisposition.AlreadyApplied, response.Disposition);
        Assert.Equal(AttemptResult.NewPersonalRecord, response.AttemptResult);
        Assert.Equal(RankState.Pending, response.RankState);
        Assert.Equal(42UL, response.RunId);
    }

    private static TimerWriteApiOptions CreateOptions()
    {
        var values = new Dictionary<string, string?>
        {
            ["TimerBackend:WriteApi:Enabled"] = "true",
            ["TimerBackend:WriteApi:RulesetVersion"] = "7",
            ["TimerBackend:WriteApi:StyleFactors:0"] = "1.5",
        };
        return TimerWriteApiOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    }

    private static SubmitRunRequest CreateRequest()
        => new ()
        {
            SubmissionId = Guid.NewGuid(),
            SteamId = 76561198000000001,
            MapName = "surf_write_rpc",
            RunKind = RunKind.Main,
            Style = 0,
            Track = 0,
            Stage = 0,
            TimeMicros = 80_000_000,
            Jumps = 5,
            Strafes = 8,
            Sync = 95,
            Motion = new MotionDto { StartX = 1, AverageY = 2, EndZ = 3 },
            Checkpoints =
            [
                new CheckpointDto
                {
                    Index = 1,
                    TimeMicros = 40_000_000,
                    Sync = 94,
                    Motion = new MotionDto { MaxX = 3 },
                },
            ],
            FinishedAtUnixTimeMilliseconds = 1_800_000_000_000,
            ContractVersion = 1,
            RulesetVersion = 7,
        };
}
