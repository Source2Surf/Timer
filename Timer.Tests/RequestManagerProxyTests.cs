using Microsoft.Extensions.Logging.Abstractions;
using Source2Surf.Timer.Managers.Request;
using Xunit;

namespace Timer.Tests;

public sealed class RequestManagerProxyTests
{
    [Fact]
    public void MissingExternalProviderFailsClosedInsteadOfOpeningLocalRecordStore()
    {
        // UseFallback is also the disconnect path; neither it nor a subsequent read may
        // instantiate a second, local authority for maps, players, or records.
        var proxy = new RequestManagerProxy(null!, NullLogger<RequestManagerProxy>.Instance);
        proxy.UseFallback();

        var failure = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = proxy.GetMapInfo("surf_test");
        });
        Assert.Contains("register its external provider", failure.Message);
    }
}
