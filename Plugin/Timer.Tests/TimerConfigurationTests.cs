using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Modules.Record;
using Xunit;

namespace Timer.Tests;

// The score-write settings, read from timer.jsonc's score_write section.
public sealed class TimerConfigurationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"timer-{Guid.NewGuid():N}.jsonc");

    [Fact]
    public void WithoutTheFileScoresAreWrittenLocally()
    {
        var config = TimerConfiguration.Load(_path);

        Assert.Equal(ScoreWriteMode.LocalSql, ScoreWriteModeOptions.FromConfiguration(config).Mode);
        Assert.False(RunSubmissionSenderOptions.FromConfiguration(config).Enabled);
    }

    [Fact]
    public void RemoteWriteIsReadFromScoreWrite()
    {
        File.WriteAllText(_path, """
                                 {
                                   "database": { "type": "postgresql" },
                                   // comments and trailing commas, as timer.jsonc has them
                                   "score_write": {
                                     "mode": "remote-write",
                                     "endpoint": "http://127.0.0.1:5082",
                                     "rpc_deadline_milliseconds": 4000,
                                     "ruleset_version": 2,
                                   },
                                 }
                                 """);

        var config = TimerConfiguration.Load(_path);
        var mode   = ScoreWriteModeOptions.FromConfiguration(config);
        var sender = RunSubmissionSenderOptions.FromConfiguration(config);

        Assert.Equal(ScoreWriteMode.RemoteWrite, mode.Mode);
        Assert.True(sender.Enabled);
        Assert.Equal(new Uri("http://127.0.0.1:5082"), sender.Endpoint);
        Assert.Equal(TimeSpan.FromMilliseconds(4000), sender.RpcDeadline);
        Assert.Equal(2, RemoteRunSubmissionOptions.FromConfiguration(config, mode).RulesetVersion);
    }

    [Fact]
    public void AnUnknownSettingFailsStartup()
    {
        File.WriteAllText(_path, """{ "score_write": { "mode": "local-sql", "endpont": "http://127.0.0.1:5082" } }""");

        Assert.Throws<InvalidOperationException>(() => RunSubmissionSenderOptions.FromConfiguration(TimerConfiguration.Load(_path)));
    }

    public void Dispose()
        => File.Delete(_path);
}
