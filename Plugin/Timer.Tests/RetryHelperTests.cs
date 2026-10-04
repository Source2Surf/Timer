using Grpc.Core;
using Source2Surf.Timer.Extensions;
using Xunit;

namespace Timer.Tests;

public sealed class RetryHelperTests
{
    [Theory]
    [InlineData(StatusCode.Unavailable, true)]
    [InlineData(StatusCode.DeadlineExceeded, true)]
    [InlineData(StatusCode.Cancelled, false)]
    [InlineData(StatusCode.InvalidArgument, false)]
    [InlineData(StatusCode.Internal, false)]
    public void BackendOutagesAreRetried(StatusCode status, bool transient)
        => Assert.Equal(transient, RetryHelper.IsTransient(new RpcException(new Status(status, "test"))));
}
