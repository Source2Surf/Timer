using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared;
using Sharp.Shared.Objects;
using Source2Surf.Timer;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Style;
using Xunit;

namespace Timer.Tests;

// A bot showing a server record that an admin deletes shows the board's next record, once its replay is in; with
// none, a looping bot goes on with its rotation and the central bot goes idle.
public sealed class ReplayRunDeletionTests
{
    private static readonly (int style, int track, int stage) Main  = (0, 0, 0);
    private static readonly (int style, int track, int stage) Bonus = (0, 1, 0);

    [Fact]
    public void ALoopingBotShowingTheDeletedRecordShowsTheBoardsNextOne()
    {
        var module  = CreateModule(wr: null);
        var deleted = Content(80f);
        var next    = Content(90f);
        var bot     = Bot(EReplayBotType.Looping);
        Cache(module)[Main]  = deleted;
        Cache(module)[Bonus] = Content(30f);
        Show(bot, Main, deleted);
        Bots(module).Add(bot);

        Invoke(module, "HoldForNextRecord", bot);

        Assert.Equal(EReplayBotStatus.Idle, bot.Status);
        Assert.Null(bot.Header);
        Assert.True(bot.AwaitsNextRecord);
        Assert.Equal(Main, (bot.Style, bot.Track, bot.Stage));

        // The next record's replay is in, as the board's load finishes.
        Cache(module)[Main] = next;
        Invoke(module, "UpdateReplayBots", 0, 0, 0);
        Invoke(module, "ResumeHeldBots", Main);

        Assert.Same(next.Header, bot.Header);
        Assert.Equal(Main, (bot.Style, bot.Track, bot.Stage));
        Assert.Equal(EReplayBotStatus.Start, bot.Status);
        Assert.False(bot.AwaitsNextRecord);
    }

    [Fact]
    public void WithNoRecordLeftALoopingBotGoesOnAndOthersCarryOn()
    {
        var module  = CreateModule(wr: null);
        var deleted = Content(80f);
        var bonus   = Content(30f);
        var showing = Bot(EReplayBotType.Looping);
        var other   = Bot(EReplayBotType.Looping);
        Cache(module)[Main]  = deleted;
        Cache(module)[Bonus] = bonus;
        Show(showing, Main, deleted);
        Show(other, Bonus, bonus);
        other.Status = EReplayBotStatus.Running;
        Bots(module).AddRange([showing, other]);

        ((IRunDeletionListener) module).OnRunDeleted("map", true, Record(1, deleted), Deleted(1));

        Assert.False(Cache(module).ContainsKey(Main));
        Assert.Same(bonus.Header, showing.Header);
        Assert.Equal(Bonus, (showing.Style, showing.Track, showing.Stage));
        Assert.False(showing.AwaitsNextRecord);

        Assert.Same(bonus.Header, other.Header);
        Assert.Equal(EReplayBotStatus.Running, other.Status);
    }

    [Fact]
    public void TheCentralBotShowingTheDeletedRecordShowsTheBoardsNextOne()
    {
        var module  = CreateModule(wr: new RunRecord { Id = 7, Style = 0, Track = 0, Time = 90f });
        var next    = Content(90f);
        var central = Bot(EReplayBotType.Central);
        Show(central, Main, Content(80f));
        central.RunId = 1;
        central.Rank  = 1;
        Bots(module).Add(central);

        Invoke(module, "HoldForNextRecord", central);

        // It stays in the game for whoever watches it.
        Assert.False(WaitsInSpectator(central));

        Cache(module)[Main] = next;
        Invoke(module, "ResumeHeldBots", Main);

        Assert.Same(next.Header, central.Header);
        Assert.Equal(7, central.RunId);
        Assert.Equal(EReplayBotStatus.Start, central.Status);
    }

    [Fact]
    public void TheCentralBotGoesIdleForAnotherRunOrWhenNoRecordIsLeft()
    {
        var module  = CreateModule(wr: null);
        var deleted = Content(80f);
        var central = Bot(EReplayBotType.Central);
        Show(central, Main, deleted);
        central.RunId = 1;
        central.Rank  = 1;
        Bots(module).Add(central);

        ((IRunDeletionListener) module).OnRunDeleted("map", true, Record(1, deleted), Deleted(1));

        Assert.Equal(EReplayBotStatus.Idle, central.Status);
        Assert.Equal(0, central.RunId);
        Assert.False(central.AwaitsNextRecord);

        // A slower run it was showing is just gone.
        var slower = Content(95f);
        Show(central, Main, slower);
        central.RunId = 2;
        central.Rank  = 3;

        ((IRunDeletionListener) module).OnRunDeleted("map", true, Record(2, slower), Deleted(2));

        Assert.Equal(EReplayBotStatus.Idle, central.Status);
        Assert.False(central.AwaitsNextRecord);
    }

    private static ReplayPlaybackModule CreateModule(RunRecord? wr)
    {
        var module = (ReplayPlaybackModule) RuntimeHelpers.GetUninitializedObject(typeof(ReplayPlaybackModule));
        SetField(module, "_replayCache", new Dictionary<(int style, int track, int stage), ReplayContent>());
        SetField(module, "_closestFrameIndices", new Dictionary<(int style, int track, int stage), ClosestFrameIndex>());
        SetField(module, "_replayBots", new List<ReplayBotData>());
        SetField(module, "_deletedReplays", new HashSet<(int style, int track, int stage, ulong steamId, float time)>());
        SetField(module, "_logger", NullLogger<ReplayPlaybackModule>.Instance);
        SetField(module, "_replayDirectory", Path.Combine(Path.GetTempPath(), "timer-tests-" + Guid.NewGuid().ToString("N")));
        SetField(module, "timer_replay_delay", Proxy<IConVar>());
        SetField(module, "_styleModule", Proxy<IStyleModule>(static (name, _) => name switch
        {
            nameof(IStyleModule.IsStyleEnabled)  => true,
            nameof(IStyleModule.GetStyleSetting) => new StyleSetting(),
            _                                    => null,
        }));
        SetField(module, "_recordModule", Proxy<IRecordModule>((name, _) => name == nameof(IRecordModule.GetWR) ? wr : null));

        var bridge = (InterfaceBridge) RuntimeHelpers.GetUninitializedObject(typeof(InterfaceBridge));
        typeof(InterfaceBridge).GetField("<ModSharp>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                               .SetValue(bridge, Proxy<IModSharp>());
        SetField(module, "_bridge", bridge);

        return module;
    }

    private static ReplayBotData Bot(EReplayBotType type)
    {
        var bot = (ReplayBotData) RuntimeHelpers.GetUninitializedObject(typeof(ReplayBotData));
        typeof(ReplayBotData).GetProperty(nameof(ReplayBotData.Config))!.SetValue(bot, new ReplayBotConfig { Type = type, Styles = [0] });
        typeof(ReplayBotData).GetProperty(nameof(ReplayBotData.Type))!.SetValue(bot, type);
        typeof(ReplayBotData).GetProperty(nameof(ReplayBotData.Client))!.SetValue(bot, Proxy<IGameClient>());
        bot.Style  = -1;
        bot.Track  = -1;
        bot.Frames = [];

        return bot;
    }

    private static void Show(ReplayBotData bot, (int style, int track, int stage) key, ReplayContent content)
    {
        (bot.Style, bot.Track, bot.Stage) = key;
        bot.Header = content.Header;
        bot.Frames = content.Frames;
        bot.Status = EReplayBotStatus.Running;
    }

    private static ReplayContent Content(float time)
        => new () { Header = new ReplayFileHeader { Time = time, SteamId = (ulong) (time * 10) }, Frames = [new ReplayFrameData()] };

    private static RunRecord Record(long id, ReplayContent content)
        => new () { Id = id, SteamId = content.Header.SteamId, Time = content.Header.Time };

    private static DeletedRun Deleted(ulong id)
        => new (id, 0, false, 0, 0, 0, true, []);

    private static Dictionary<(int style, int track, int stage), ReplayContent> Cache(ReplayPlaybackModule module)
        => (Dictionary<(int style, int track, int stage), ReplayContent>) GetField(module, "_replayCache")!;

    private static List<ReplayBotData> Bots(ReplayPlaybackModule module)
        => (List<ReplayBotData>) GetField(module, "_replayBots")!;

    private static bool WaitsInSpectator(ReplayBotData bot)
        => (bool) typeof(ReplayPlaybackModule).GetMethod("WaitsInSpectator", BindingFlags.Static | BindingFlags.NonPublic)!
                                              .Invoke(null, [bot])!;

    private static void Invoke(ReplayPlaybackModule module, string method, params object[] args)
        => typeof(ReplayPlaybackModule).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, args);

    private static object? GetField(object target, string name)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void SetField(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Proxy<T>(Func<string, object?[]?, object?>? answer = null)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, DefaultsProxy>();
        ((DefaultsProxy) (object) proxy).Answer = answer;

        return proxy;
    }

    // Answers what it's told to, and anything else with the return type's default.
    public class DefaultsProxy : DispatchProxy
    {
        public Func<string, object?[]?, object?>? Answer { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Answer?.Invoke(targetMethod!.Name, args) is { } answer)
            {
                return answer;
            }

            var type = targetMethod!.ReturnType;

            return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
        }
    }
}
