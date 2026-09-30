using System;
using Source2Surf.Timer.Shared.Models;
using Timer.Backend.Mapping;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerDtoMapperTests
{
    [Fact]
    public void RunRecordMappingPreservesUnsignedBitsAndNormalizesUtc()
    {
        var storedDate = new DateTime(2026, 9, 15, 4, 5, 6, DateTimeKind.Unspecified);
        var record = new RunRecord
        {
            Id         = -1,
            RunDate    = storedDate,
            SteamId    = ulong.MaxValue,
            PlayerName = string.Empty,
            MapId      = 9,
            Time       = 1.25f,
        };

        var dto = TimerDtoMapper.ToDto(record);

        Assert.Equal(ulong.MaxValue.ToString(), dto.Id);
        Assert.Equal(ulong.MaxValue.ToString(), dto.SteamId);
        Assert.Equal("9", dto.MapId);
        Assert.Null(dto.PlayerName);
        Assert.Equal(1_250_000, dto.TimeMicros);
        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(storedDate, DateTimeKind.Utc))
                         .ToUnixTimeMilliseconds(),
                     dto.RunDate);
    }

    [Fact]
    public void MapProfileMappingCopiesTierArray()
    {
        var source = new MapProfile
        {
            MapId        = 42,
            MapName      = "surf_contract",
            Tier         = [1, 6],
            TotalPlayTime = 2.5f,
        };

        var dto = TimerDtoMapper.ToDto(source);
        source.Tier[0] = 9;

        Assert.Equal("42", dto.MapId);
        Assert.Equal(2_500_000, dto.TotalPlayTimeMicros);
        Assert.Equal(new[] { 1, 6 }, dto.Tier);
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void InvalidStoredDurationIsRejected(float seconds)
        => Assert.Throws<InvalidOperationException>(() => TimerDtoMapper.ToMicroseconds(seconds));

    [Fact]
    public void OversizedStoredDurationIsRejected()
        => Assert.Throws<OverflowException>(() => TimerDtoMapper.ToMicroseconds(float.MaxValue));

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.MaxValue)]
    public void UnrepresentableRecordTimeIsReportedWithoutThrowing(float seconds)
    {
        var record = new RunRecord { Id = 1, SteamId = 2, MapId = 3, Time = seconds, RunDate = DateTime.UtcNow };

        Assert.False(TimerDtoMapper.TryToDto(record, out var dto));
        Assert.Null(dto);
        Assert.Equal(0, TimerDtoMapper.ToMicrosecondsOrZero(seconds));
    }

    [Theory]
    [InlineData(0L, "0")]
    [InlineData(-1L, "18446744073709551615")]
    [InlineData(long.MinValue, "9223372036854775808")]
    public void SignedStorageIdentityUsesRawUnsignedRepresentation(long value, string expected)
        => Assert.Equal(expected, TimerDtoMapper.ToSignedId(value));
}
