using System;
using Microsoft.AspNetCore.Http;
using Source2Surf.Timer.Backend.Contracts;
using Timer.Backend.Endpoints;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class EntityTagsTests
{
    [Fact]
    public void SameResponseProducesStableWeakTag()
    {
        var response = CreateResponse("Nuko");

        var first = EntityTags.For(response);
        var second = EntityTags.For(response);

        Assert.StartsWith("W/\"", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
    }

    [Fact]
    public void UnicodePlayerNamesRetainAllCodeUnitsInCheckedBuilds()
    {
        var first = EntityTags.For(CreateResponse("玩家😀"));
        Assert.Equal(first, EntityTags.For(CreateResponse("玩家😀")));
        Assert.NotEqual(first, EntityTags.For(CreateResponse("玩家😁")));
    }

    [Fact]
    public void MaterialFieldChangeInvalidatesTag()
        => Assert.NotEqual(EntityTags.For(CreateResponse("Nuko")),
                           EntityTags.For(CreateResponse("Kxnrl")));

    [Fact]
    public void RunDateMillisecondChangeInvalidatesTag()
    {
        var first = CreateResponse("Nuko");
        var second = CreateResponse("Nuko");
        second.Records[0] = CreateRecord("Nuko", runDate: first.Records[0].RunDate + 1);

        Assert.NotEqual(EntityTags.For(first), EntityTags.For(second));
    }

    [Fact]
    public void CheckpointPresenceInvalidatesTag()
    {
        var withoutCheckpoints = CreateResponse("Nuko");
        var withCheckpoints = CreateResponse("Nuko");
        withCheckpoints.Records[0] = CreateRecord("Nuko", [new RunCheckpointDto
        {
            Id              = "10",
            RecordId        = "1",
            CheckpointIndex = 1,
            TimeMicros      = 500_000,
        }]);

        Assert.NotEqual(EntityTags.For(withoutCheckpoints), EntityTags.For(withCheckpoints));
    }

    [Theory]
    [InlineData("{0}", true)]
    [InlineData("{1}", true)]
    [InlineData("\"other\", {0}", true)]
    [InlineData("*", true)]
    [InlineData("\"other\"", false)]
    public void IfNoneMatchUsesWeakComparison(string headerTemplate, bool expected)
    {
        var tag = EntityTags.For(CreateResponse("Nuko"));
        var strongTag = tag[2..];
        var context = new DefaultHttpContext();
        context.Request.Headers.IfNoneMatch = string.Format(headerTemplate, tag, strongTag);

        Assert.Equal(expected, EntityTags.Matches(context.Request, tag));
    }

    private static RecordListResponse CreateResponse(string playerName)
        => new ()
        {
            MapName = "surf_contract",
            Records = [CreateRecord(playerName)],
        };

    private static RunRecordDto CreateRecord(string playerName,
                                              RunCheckpointDto[]? checkpoints = null,
                                              long? runDate = null)
        => new ()
        {
            Id          = "1",
            RunDate     = runDate ?? new DateTimeOffset(2026, 9, 15, 1, 2, 3, TimeSpan.Zero)
                .ToUnixTimeMilliseconds(),
            SteamId     = "76561198000000000",
            PlayerName  = playerName,
            MapId       = "2",
            Style       = 0,
            Track       = 0,
            Stage       = 0,
            TimeMicros  = 1_000_000,
            Checkpoints = checkpoints,
        };
}
