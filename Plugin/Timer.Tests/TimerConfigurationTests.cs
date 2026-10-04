using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Modules.Record;
using Xunit;

namespace Timer.Tests;

// The backend settings, read from timer.jsonc's backend section.
public sealed class TimerConfigurationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-{Guid.NewGuid():N}.jsonc");

    [Fact]
    public void WithoutTheFileStartupAsksForTheEndpoint()
    {
        var config = TimerConfiguration.Load(_path);

        Assert.Throws<InvalidOperationException>(() => BackendOptions.FromConfiguration(config));
    }

    [Fact]
    public void BackendIsReadFromItsSection()
    {
        File.WriteAllText(_path, """
                                 {
                                   "database": { "type": "postgresql" },
                                   // comments and trailing commas, as timer.jsonc has them
                                   "backend": {
                                     "endpoint": "http://127.0.0.1:5082",
                                     "rpc_deadline_milliseconds": 4000,
                                     "ruleset_version": 2,
                                   },
                                 }
                                 """);

        var config  = TimerConfiguration.Load(_path);
        var backend = BackendOptions.FromConfiguration(config);

        Assert.Equal(new Uri("http://127.0.0.1:5082"), backend.Endpoint);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), backend.RpcDeadline);
        Assert.Equal(2, RemoteRunSubmissionOptions.FromConfiguration(config).RulesetVersion);
    }

    [Fact]
    public void AnUnknownSettingFailsStartup()
    {
        File.WriteAllText(_path, """{ "backend": { "endpoint": "http://127.0.0.1:5082", "endpont": "http://127.0.0.1:5082" } }""");

        Assert.Throws<InvalidOperationException>(() => BackendOptions.FromConfiguration(TimerConfiguration.Load(_path)));
    }

    public void Dispose()
        => File.Delete(_path);
}
