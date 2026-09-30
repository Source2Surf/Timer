using System;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Managers.Player;
using Xunit;

namespace Timer.Tests;

public sealed class BackendPlayerProfileMapperTests
{
    private const long RequestedSteamId = 7_656_119_800_000_000_001;

    [Fact]
    public void MapsScalarResponseAndNormalizesTimestampsToUtc()
    {
        const long joinMilliseconds     = 1_725_000_000_000;
        const long lastSeenMilliseconds = 1_725_000_001_000;
        var requested = new SteamID((ulong) RequestedSteamId);

        var profile = BackendPlayerProfileMapper.ToProfile(new EnsurePlayerProfileResponse
        {
            PlayerId                         = 42,
            SteamId                          = RequestedSteamId,
            Name                            = "Runner",
            Points                          = 123,
            JoinDateUnixTimeMilliseconds     = joinMilliseconds,
            LastSeenDateUnixTimeMilliseconds = lastSeenMilliseconds,
        }, requested);

        Assert.Equal(42, profile.Id);
        Assert.Equal(requested, profile.SteamId);
        Assert.Equal("Runner", profile.Name);
        Assert.Equal((uint) 123, profile.Points);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(joinMilliseconds).UtcDateTime, profile.JoinDate);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(lastSeenMilliseconds).UtcDateTime, profile.LastSeenDate);
        Assert.Equal(DateTimeKind.Utc, profile.JoinDate.Kind);
        Assert.Equal(DateTimeKind.Utc, profile.LastSeenDate.Kind);
    }

    [Fact]
    public void RejectsNullResponse()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BackendPlayerProfileMapper.ToProfile(null!, new SteamID((ulong) RequestedSteamId)));
    }

    [Theory]
    [InlineData(0, 7_656_119_800_000_000_001L)]
    [InlineData(42, 7_656_119_800_000_000_002L)]
    [InlineData(42, 0)]
    public void RejectsInvalidPlayerIdentity(long playerId, long responseSteamId)
    {
        var response = CreateResponse();
        response.PlayerId = playerId;
        response.SteamId = responseSteamId;

        Assert.Throws<InvalidOperationException>(() =>
            BackendPlayerProfileMapper.ToProfile(response, new SteamID((ulong) RequestedSteamId)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Runner\n")]
    public void RejectsInvalidNames(string? name)
    {
        var response = CreateResponse();
        response.Name = name!;

        Assert.Throws<InvalidOperationException>(() =>
            BackendPlayerProfileMapper.ToProfile(response, new SteamID((ulong) RequestedSteamId)));
    }

    [Fact]
    public void RejectsNamesLongerThanTheStorageBound()
    {
        var response = CreateResponse();
        response.Name = new string('x', 193);

        Assert.Throws<InvalidOperationException>(() =>
            BackendPlayerProfileMapper.ToProfile(response, new SteamID((ulong) RequestedSteamId)));
    }

    [Theory]
    [InlineData(long.MinValue, 1_725_000_001_000L)]
    [InlineData(long.MaxValue, 1_725_000_001_000L)]
    [InlineData(1_725_000_000_000L, long.MinValue)]
    [InlineData(1_725_000_000_000L, long.MaxValue)]
    public void RejectsOutOfRangeUnixMilliseconds(long joinMilliseconds, long lastSeenMilliseconds)
    {
        var response = CreateResponse();
        response.JoinDateUnixTimeMilliseconds = joinMilliseconds;
        response.LastSeenDateUnixTimeMilliseconds = lastSeenMilliseconds;

        Assert.Throws<InvalidOperationException>(() =>
            BackendPlayerProfileMapper.ToProfile(response, new SteamID((ulong) RequestedSteamId)));
    }

    private static EnsurePlayerProfileResponse CreateResponse()
        => new()
        {
            PlayerId                         = 42,
            SteamId                          = RequestedSteamId,
            Name                            = "Runner",
            Points                          = 123,
            JoinDateUnixTimeMilliseconds     = 1_725_000_000_000,
            LastSeenDateUnixTimeMilliseconds = 1_725_000_001_000,
        };
}
