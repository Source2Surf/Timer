using System;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Timer.Backend.WriteApi;
using Timer.Backend.Storage;
using Xunit;

namespace Timer.Backend.Tests;

public sealed class TimerWriteRpcErrorsTests
{
    [Theory]
    [InlineData(StatusCode.InvalidArgument)]
    [InlineData(StatusCode.NotFound)]
    [InlineData(StatusCode.AlreadyExists)]
    public void DomainExceptionsMapToStableGrpcCodes(StatusCode expected)
    {
        Exception exception = expected switch
        {
            StatusCode.InvalidArgument => new TimerBackendSubmissionValidationException("private validation detail"),
            StatusCode.NotFound => new TimerBackendPlayerNotFoundException(76561198000000001),
            StatusCode.AlreadyExists => new TimerBackendSubmissionConflictException(Guid.NewGuid()),
            _ => throw new InvalidOperationException(),
        };

        var translated = TimerWriteRpcErrors.Translate(exception, NullLogger.Instance);
        Assert.Equal(expected, translated.StatusCode);
    }

    [Fact]
    public void DatabaseOutageMapsToUnavailableWithoutDetails()
    {
        var translated = TimerWriteRpcErrors.Translate(
            new TimerBackendUnavailableException(new InvalidOperationException("private connection detail")),
            NullLogger.Instance);

        Assert.Equal(StatusCode.Unavailable, translated.StatusCode);
        Assert.DoesNotContain("private", translated.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerProfileValidationMapsToInvalidArgumentWithoutDetails()
    {
        var translated = TimerWriteRpcErrors.Translate(
            new TimerBackendPlayerProfileValidationException("private profile detail"),
            NullLogger.Instance);

        Assert.Equal(StatusCode.InvalidArgument, translated.StatusCode);
        Assert.Equal("The request is invalid.", translated.Status.Detail);
    }

    [Fact]
    public void ExistingRpcAndUnknownFailuresPreserveOrUseCorrectStatus()
    {
        var cancelled = new RpcException(new Status(StatusCode.Cancelled, "caller cancelled"));
        Assert.Same(cancelled, TimerWriteRpcErrors.Translate(cancelled, NullLogger.Instance));

        var unknown = TimerWriteRpcErrors.Translate(new InvalidOperationException("private detail"), NullLogger.Instance);
        Assert.Equal(StatusCode.Internal, unknown.StatusCode);
        Assert.Equal("The request could not be completed.", unknown.Status.Detail);
    }

    [Fact]
    public void CancellationAndDeadlineRemainDistinctGrpcStatuses()
    {
        Assert.Equal(StatusCode.Cancelled,
                     TimerWriteRpcErrors.CancellationOrDeadline(DateTime.MaxValue).StatusCode);
        Assert.Equal(StatusCode.DeadlineExceeded,
                     TimerWriteRpcErrors.CancellationOrDeadline(DateTime.UtcNow.AddSeconds(-1)).StatusCode);
    }
}
