using System.Reflection;
using System.Runtime.CompilerServices;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

public sealed class ReplayPlaybackBotTests
{
    [Theory]
    [InlineData(0, 0, true)]   // allowed style, main track
    [InlineData(1, 0, false)]  // style not in the bot's config
    [InlineData(0, 1, false)]  // bonus track on a MainOnly bot
    public void IdleBotOnlyMatchesReplaysItsConfigAllows(int style, int track, bool expected)
    {
        var bot = CreateBot(new ReplayBotConfig { PlayType = EReplayBotPlayType.MainOnly, Styles = [0] });

        Assert.Equal(expected, InvokeIsReplayBotMatch(bot, style, track, stage: 0));
    }

    [Fact]
    public void StageBotRotatesToTheNextStageReplay()
    {
        var module = CreatePlaybackModule(styleCount: 1);
        var cache = (Dictionary<(int style, int track, int stage), ReplayContent>) GetField(module, "_replayCache")!;
        cache[(0, 0, 1)] = CreateContent();
        cache[(0, 0, 2)] = CreateContent();

        var bot = CreateBot(new ReplayBotConfig { StageBot = true, Styles = [0] });
        bot.Style = 0;
        bot.Track = 0;
        bot.Stage = 1;

        typeof(ReplayPlaybackModule).GetMethod("FindNextStageReplay", BindingFlags.Instance | BindingFlags.NonPublic)!
                                    .Invoke(module, [bot]);

        Assert.Equal(2, bot.Stage);
    }

    private static bool InvokeIsReplayBotMatch(ReplayBotData bot, int style, int track, int stage)
        => (bool) typeof(ReplayPlaybackModule).GetMethod("IsReplayBotMatch", BindingFlags.Static | BindingFlags.NonPublic)!
                                              .Invoke(null, [bot, style, track, stage])!;

    private static ReplayBotData CreateBot(ReplayBotConfig config)
    {
        // Controller/Client are engine objects that these code paths never touch.
        var bot = (ReplayBotData) RuntimeHelpers.GetUninitializedObject(typeof(ReplayBotData));
        typeof(ReplayBotData).GetProperty(nameof(ReplayBotData.Config))!.SetValue(bot, config);
        bot.Style = -1;
        bot.Track = -1;
        bot.Stage = 0;
        bot.Frames = [];
        return bot;
    }

    private static ReplayPlaybackModule CreatePlaybackModule(int styleCount)
    {
        var module = (ReplayPlaybackModule) RuntimeHelpers.GetUninitializedObject(typeof(ReplayPlaybackModule));
        SetField(module, "_replayCache", new Dictionary<(int style, int track, int stage), ReplayContent>());
        var styles = DispatchProxy.Create<IStyleModule, StyleCountProxy>();
        ((StyleCountProxy) (object) styles).StyleCount = styleCount;
        SetField(module, "_styleModule", styles);
        return module;
    }

    private static ReplayContent CreateContent()
        => new () { Header = new ReplayFileHeader { Time = 10f }, Frames = [] };

    private static object? GetField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public class StyleCountProxy : DispatchProxy
    {
        public int StyleCount { get; set; }

        // Styles 0 to StyleCount - 1, all enabled.
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name switch
            {
                nameof(IStyleModule.IsStyleEnabled) => (int) args![0]! < StyleCount,
                nameof(IStyleModule.GetStyleIds)    => Enumerable.Range(0, StyleCount).ToArray(),
                _                                   => throw new NotSupportedException(targetMethod?.Name),
            };
    }
}
