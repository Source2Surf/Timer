using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer;
using Source2Surf.Timer.Modules;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

public sealed class HudFormatTests
{
    [Theory]
    [InlineData(0f, "0.000")]
    [InlineData(10f, "10.000")]
    [InlineData(14.203125f, "14.203")]       // truncated, like every timer
    [InlineData(65.5f, "01:05.500")]         // minutes once there are any
    [InlineData(600f, "10:00.000")]
    [InlineData(3723.456f, "01:02:03.456")]  // hours only past an hour
    [InlineData(-5f, "0.000")]               // the HUD never shows a negative time
    public void FormatsTimesTheWayTheTimerDoes(float seconds, string expected)
        => Assert.Equal(expected, HudFormat.FormatTime(seconds));

    [Fact]
    public void DifferencesAgreeWithTheTimesOnScreen()
    {
        // 30.000 against 14.203 must read 15.797, not the 15.796 the raw 15.796875 seconds would show.
        var diff = HudFormat.DiffMillis(30f, 14.203125f);

        Assert.Equal(15797, diff);
        Assert.Equal("+15.797", HudFormat.FormatDiff(diff));
        Assert.Equal("-15.797", HudFormat.FormatDiff(-diff));
        Assert.Equal("+01:02.030", HudFormat.FormatDiff(62030));
        Assert.Equal("+0.000", HudFormat.FormatDiff(0));
    }

    [Theory]
    [InlineData(5.123f, true, "5.123")]
    [InlineData(5.19f, false, "5.1")]           // tenths, truncated
    [InlineData(65.55f, false, "01:05.5")]
    [InlineData(-1.5f, true, "-1.500")]         // chat deltas keep their sign
    [InlineData(-0.0001f, true, "0.000")]       // but not on a time that shows as zero
    [InlineData(7322.5f, false, "02:02:02.5")]
    [InlineData(36000f, true, "10:00:00.000")]
    [InlineData(float.NaN, true, "0.000")]
    public void UtilsFormatsTimesThatGrowWithThem(float seconds, bool precise, string expected)
        => Assert.Equal(expected, Utils.FormatTime(seconds, precise));

    [Theory]
    [InlineData(null, 0, 0, 1, "SR")]
    [InlineData(null, 0, 3, 4, "Stage 3 #4")]
    [InlineData(null, 2, 0, 1, "Bonus 2 SR")]
    [InlineData("Sideways", 1, 0, 12, "Sideways Bonus 1 #12")]
    [InlineData(null, 0, 0, 0, "Run")]
    [InlineData(null, 0, 2, 0, "Stage 2 Run")]
    public void TagsAReplayByStyleTrackStageAndRank(string? style, int track, int stage, int rank, string expected)
        => Assert.Equal(expected, HudFormat.ReplayTag(HudTr.English, style, track, stage, rank));

    [Theory]
    [InlineData("aoba", 1, false, "aoba's SR")]
    [InlineData("tofu", 4, false, "tofu's #4")]
    [InlineData("tofu", 4, true, "your run")]
    [InlineData("mizu", 0, false, "mizu's run")]
    public void SaysWhoseRunItIs(string name, int rank, bool you, string expected)
        => Assert.Equal(expected, HudFormat.Whose(HudTr.English, name, rank, you));

    [Theory]
    [InlineData(0f, "0 min")]
    [InlineData(12 * 60 + 59f, "12 min")]
    [InlineData(3 * 3600 + 12 * 60f, "3 h 12 min")]
    [InlineData(96 * 3600 + 1800f, "96 h")]
    [InlineData(1240 * 3600f, "1,240 h")]
    [InlineData(float.NaN, "0 min")]
    public void SaysHowLongWasPlayed(float seconds, string expected)
        => Assert.Equal(expected, HudFormat.Duration(HudTr.English, seconds));

    [Theory]
    [InlineData(41, 212, "of 212 · 19%")]
    [InlineData(211, 212, "of 212 · 99%")]
    [InlineData(5, 2427, "of 2,427 · 0%")]
    [InlineData(0, 0, "of 0")]
    public void SaysHowMuchOfTheTotalIsDone(int done, int total, string expected)
        => Assert.Equal(expected, HudFormat.OfTotal(HudTr.English, done, total));

    [Fact]
    public void NamesYourOwnRunsByTheirTime()
        => Assert.Equal("your 31.200 run", HudFormat.OwnRun(HudTr.English, 31.2f));

    [Theory]
    [InlineData(20, "Just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600 + 59 * 60, "3 h ago")]
    [InlineData(30 * 3600, "1 day ago")]
    [InlineData(5 * 86400, "5 days ago")]
    [InlineData(40 * 86400, "2026-08-23")]
    public void SaysHowLongAgoARunWasSet(int secondsAgo, string expected)
    {
        var now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(expected, HudFormat.Ago(HudTr.English, now.AddSeconds(-secondsAgo), now));
    }

    [Theory]
    [InlineData(0.5f, "0.5×")]
    [InlineData(1f, "1×")]
    [InlineData(2f, "2×")]
    public void TagsPlaybackSpeed(float speed, string expected)
        => Assert.Equal(expected, HudFormat.SpeedTag(speed));

    [Theory]
    [InlineData(0f, 30f, 0)]
    [InlineData(15f, 30f, 25)]
    [InlineData(30f, 30f, 50)]
    [InlineData(31f, 30f, 50)]
    [InlineData(5f, 0f, 0)]
    [InlineData(float.NaN, 30f, 0)]
    public void StepsProgressInTwoPercents(float elapsed, float total, int expected)
        => Assert.Equal(expected, HudFormat.ProgressStep(elapsed, total));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(23, 3)]
    public void CountsPagesOfEight(int count, int expected)
        => Assert.Equal(expected, HudFormat.PageCount(count, 8));

    [Fact]
    public void ReplayTimeStartsWhereTheRunDoes()
    {
        // 64 pre-run frames, then the run: the bot at frame 64 is at 00:00:00.
        Assert.Equal(0f, HudFormat.ReplayElapsed(10, 64, 1000));
        Assert.Equal(0f, HudFormat.ReplayElapsed(64, 64, 1000));
        Assert.Equal(1f, HudFormat.ReplayElapsed(128, 64, 1000));
        Assert.Equal(HudFormat.ReplayElapsed(1000, 64, 1000), HudFormat.ReplayElapsed(1200, 64, 1000));
    }

    [Fact]
    public void NonFiniteAndHugeTimesNeitherThrowNorOverflow()
    {
        Assert.Equal(0, Utils.Millis(float.NaN));
        Assert.Equal(0, Utils.Millis(float.PositiveInfinity));

        var huge = Utils.Millis(float.MaxValue);
        Assert.True(huge > 0);
        Assert.NotEmpty(HudFormat.FormatTime(float.MaxValue));
    }

    [Theory]
    [InlineData(16f, "+16 u/s")]
    [InlineData(-100f, "-100 u/s")]
    [InlineData(0.4f, "+0 u/s")]
    [InlineData(float.NaN, "+0 u/s")]
    public void FormatsSpeedDifferences(float difference, string expected)
        => Assert.Equal(expected, HudFormat.FormatSpeedDiff(difference));

    [Theory]
    [InlineData(300f, 302f, 1)]
    [InlineData(900f, 902f, 1)]      // small gains at high speed still count
    [InlineData(1500f, 1501f, 1)]
    [InlineData(900f, 899f, -1)]
    [InlineData(1500f, 1490f, -1)]
    [InlineData(900.2f, 900.4f, 0)]  // the shown number didn't change
    [InlineData(250f, 250f, 0)]
    [InlineData(float.NaN, 250f, 1)]
    public void SpeedColourFollowsTheShownNumberAtAnySpeed(float previous, float current, int expected)
        => Assert.Equal(expected, HudFormat.SpeedTrend(previous, current));

    [Theory]
    [InlineData(-37, "m4", "3")]
    [InlineData(-45, "m5", "5")]
    [InlineData(-50, "m5", "0")]
    [InlineData(0, "0", "0")]
    [InlineData(3, "0", "3")]
    [InlineData(47, "4", "7")]
    [InlineData(50, "5", "0")]
    public void SplitsOffsetsIntoTensAndUnits(int offset, string tens, string units)
    {
        Assert.Equal((tens, units), HudFormat.SplitOffset(offset));
    }

    [Fact]
    public void EveryReachableOffsetHasItsPositionClasses()
    {
        var css     = File.ReadAllText(HudAssets.Style("hud_positions.css"));
        var defined = Regex.Matches(css, @"^\.([mf][xy]-m?\d+) \{", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToHashSet();

        for (var offset = -50; offset <= 50; offset++)
        {
            var (tens, units) = HudFormat.SplitOffset(offset);

            Assert.Contains($"mx-{tens}", defined);
            Assert.Contains($"my-{tens}", defined);
            Assert.Contains($"fx-{units}", defined);
            Assert.Contains($"fy-{units}", defined);
        }

        Assert.True(defined.Count <= 42, "the position class vocabulary stays small");
    }

    [Fact]
    public void ShownOffsetsAreWholePercentsAndSnappedAxesAreCentred()
    {
        Assert.Equal(0, HudFormat.ShownOffset(2.4f, true));
        Assert.Equal(-12, HudFormat.ShownOffset(-12.4f, false));
        Assert.Equal(50, HudFormat.ShownOffset(80f, false));
        Assert.Equal(0, HudFormat.ShownOffset(float.NaN, false));
    }

    [Fact]
    public void SnappingCatchesNearTheCentreAndLetsGoFurtherOut()
    {
        Assert.True(HudFormat.SnapsToCentre(1.4f, false));
        Assert.False(HudFormat.SnapsToCentre(2f, false));
        Assert.True(HudFormat.SnapsToCentre(2f, true));   // held until the release distance
        Assert.False(HudFormat.SnapsToCentre(3.1f, true));
    }

    [Fact]
    public void BlankLineOnlyShowsBetweenShownLines()
    {
        // Zone, [blank], Mode
        Assert.True(HudFormat.GapShown([true, false, true], 1));
        // nothing above it
        Assert.False(HudFormat.GapShown([false, false, true], 1));
        // at the top
        Assert.False(HudFormat.GapShown([false, true, true], 0));
        // nothing below it (the finish layout keeps only the end speed above)
        Assert.False(HudFormat.GapShown([true, false, false, false], 1));
    }

    [Fact]
    public void HeadingSpacersFollowShownGroupsWithSomethingAfterThem()
    {
        // title, time, stage, lines
        Assert.Equal([true, true, false], HudFormat.SpacersShown([true, true, false, true]));
        Assert.Equal([false, false, false], HudFormat.SpacersShown([false, false, false, true]));
        Assert.Equal([false, true, false], HudFormat.SpacersShown([false, true, true, false]));
        Assert.Equal([false, false, false], HudFormat.SpacersShown([false, false, true, false]));
    }
}

public sealed class HudOptionsTests
{
    [Fact]
    public void OptionsHaveUniqueIdsAndIndices()
    {
        Assert.Equal(HudOptions.All.Length, HudOptions.ById.Count);

        for (var i = 0; i < HudOptions.All.Length; i++)
        {
            Assert.Equal(i, HudOptions.All[i].Index);
        }
    }

    [Fact]
    public void LiveDifferenceIsGreyedOutOnlyWithoutAComparison()
    {
        var settings = HudOptions.NewSettings();

        // Against your PB (the default) or the server record, it follows its switch.
        foreach (var compare in new[] { HudOptions.ComparePersonalBest, HudOptions.CompareServerRecord })
        {
            settings[HudOptions.Compare.Index] = compare;
            var (choice, disabled) = HudOptions.Display(HudOptions.Live, settings);
            Assert.False(disabled);
            Assert.Equal("On", choice.Label);
        }

        settings[HudOptions.Compare.Index] = HudOptions.CompareOff;
        var (off, greyed) = HudOptions.Display(HudOptions.Live, settings);
        Assert.True(greyed);
        Assert.Equal("Off", off.Label);
    }

    [Fact]
    public void DefaultsMatchTheDesign()
    {
        var settings = HudOptions.NewSettings();

        Assert.False(HudOptions.IsOn(settings, HudOptions.Keys));     // off until !showkeys
        Assert.False(HudOptions.IsOn(settings, HudOptions.Jumps));
        Assert.True(HudOptions.IsOn(settings, HudOptions.Zone));
        Assert.Equal("100%", HudOptions.SizeRun.Choices[settings[HudOptions.SizeRun.Index]].Label);
        Assert.Equal("5", HudOptions.SplitRows.Choices[settings[HudOptions.SplitRows.Index]].Label);
        Assert.Equal(HudOptions.ComparePersonalBest, settings[HudOptions.Compare.Index]);
    }

    [Fact]
    public void SavedOrdersMustHoldEveryLineOnce()
    {
        var names = HudLines.DefaultOrder.Select(l => l.ToString()).ToList();
        Assert.Equal(HudLines.DefaultOrder, HudLines.Parse(names));

        names.Reverse();
        Assert.Equal(HudLines.DefaultOrder.Reverse(), HudLines.Parse(names));

        Assert.Null(HudLines.Parse(["Mode", "Mode"]));
        Assert.Null(HudLines.Parse(names.Take(7).ToList()));
        Assert.Null(HudLines.Parse(names.Select(n => n == "Sync" ? "Mode" : n).ToList()));
        Assert.Null(HudLines.Parse(names.Select(n => n == "Sync" ? "Nope" : n).ToList()));
        Assert.Null(HudLines.Parse(names.Select(n => n == "Sync" ? "42" : n).ToList()));
        Assert.Null(HudLines.Parse(null));
    }
}

public sealed class HudPlayerTests
{
    [Fact]
    public void KeepsTheNewestSplitsFirst()
    {
        var p = new HudPlayer(new PlayerSlot(0), 0);

        for (var i = 1; i <= 10; i++)
        {
            p.AddSplit(new HudSplit(false, i, i, null, null, i, null, null));
        }

        Assert.Equal(HudPlayer.MaxSplits, p.Splits.Count);
        Assert.Equal(10, p.Splits[0].Number);
        Assert.Equal(3, p.Splits[^1].Number);
        Assert.Equal(10, p.SplitSerial);
    }

    [Fact]
    public void ANewAttemptClearsTheRunWithoutReplayingTheAnimation()
    {
        var p = new HudPlayer(new PlayerSlot(0), 0);
        p.AddSplit(new HudSplit(true, 1, 10, null, null, 10, null, null));
        p.Finish  = new HudFinish(0, 30, null, null, null, 1000, null, null, false);
        p.Stopped = true;

        p.ClearRun();

        Assert.Empty(p.Splits);
        Assert.Null(p.Finish);
        Assert.False(p.Stopped);
        Assert.Equal(p.SplitSerial, p.SplitShown);
    }
}

public sealed class HudSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"hud-settings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void SettingsSurviveASaveAndLoad()
    {
        var store = new HudSettingsStore(_directory, NullLogger.Instance);
        var p     = new HudPlayer(new PlayerSlot(1), 0);

        p.Settings[HudOptions.SizeRun.Index] = Array.IndexOf(HudOptions.Sizes, 130);
        p.Settings[HudOptions.Keys.Index]    = 0;
        p.Order                              = HudLines.DefaultOrder.Reverse().ToArray();
        p.Positions[(int) HudTarget.Run]     = new HudPosition { X = 2.4f, Y = 13.6f, Cx = true };
        p.Positions[(int) HudTarget.Info]    = new HudPosition { X = 50f, Y = -47f };

        store.Save(76561198000000001, HudSettingsStore.Capture(p), true);

        var loaded = new HudPlayer(new PlayerSlot(1), 0);
        HudSettingsStore.Apply(store.Load(76561198000000001)!, loaded);

        Assert.Equal(p.Settings, loaded.Settings);
        Assert.Equal(p.Order, loaded.Order);
        Assert.Equal(0, loaded.Positions[(int) HudTarget.Run]!.X);     // snapped: exactly on the centre line
        Assert.Equal(14, loaded.Positions[(int) HudTarget.Run]!.Y);
        Assert.Equal(50, loaded.Positions[(int) HudTarget.Info]!.X);
        Assert.Null(loaded.Positions[(int) HudTarget.Menu]);
    }

    [Fact]
    public void APanelGoingBackToItsDefaultSpotIsNotSaved()
    {
        var p = new HudPlayer(new PlayerSlot(1), 0);
        p.Positions[(int) HudTarget.Run] = new HudPosition { X = 3 };
        p.UnplaceAt[(int) HudTarget.Run] = 1;

        Assert.Empty(HudSettingsStore.Capture(p).Placement);
    }

    [Fact]
    public void UnknownOrInvalidSavedValuesAreIgnored()
    {
        var saved = new HudSavedSettings
        {
            Options   = new Dictionary<string, string> { ["OptZone"] = "Maybe", ["SizeRun"] = "120%", ["Gone"] = "On" },
            LineOrder = ["Mode", "Mode"],
        };

        var p = new HudPlayer(new PlayerSlot(1), 0);
        HudSettingsStore.Apply(saved, p);

        Assert.True(p.IsOn(HudOptions.Zone));
        Assert.Equal("120%", p.Setting(HudOptions.SizeRun));
        Assert.Equal(HudLines.DefaultOrder, p.Order);
    }

    [Fact]
    public void MissingOrCorruptFilesLoadAsNothing()
    {
        var store = new HudSettingsStore(_directory, NullLogger.Instance);
        Assert.Null(store.Load(1));

        File.WriteAllText(Path.Combine(_directory, "hud", "2.json"), "{ not json");
        Assert.Null(store.Load(2));
    }
}

/// <summary>
///     The plugin and the layout it drives must agree on every panel id and class name.
/// </summary>
public sealed class HudLayoutContractTests
{
    private static readonly string Xml = File.ReadAllText(HudAssets.Layout());

    private static readonly string Css = File.ReadAllText(HudAssets.Style("hud.css"))
                                         + File.ReadAllText(HudAssets.Style("hud_positions.css"));

    private static readonly HashSet<string> Ids =
        Regex.Matches(Regex.Replace(Xml, "<!--.*?-->", "", RegexOptions.Singleline), "\\bid=\"([^\"]+)\"")
             .Select(m => m.Groups[1].Value)
             .ToHashSet();

    [Fact]
    public void EveryOptionHasItsButtonAndValueLabel()
    {
        foreach (var option in HudOptions.All)
        {
            if (HudLines.RowOptions.Contains(option))
            {
                continue; // switched from the Timer tab's rows
            }

            Assert.Contains(option.ValueId, Ids);

            if (option.IsSize)
            {
                Assert.Contains(option.DownId, Ids);
                Assert.Contains(option.UpId, Ids);
            }
            else
            {
                Assert.Contains(option.Id, Ids);
            }

            foreach (var panel in option.Panels)
            {
                Assert.Contains(panel, Ids);
            }
        }
    }

    [Fact]
    public void EveryLineSlotRowTabAndPanelExists()
    {
        for (var i = 0; i < HudLines.Count; i++)
        {
            Assert.Contains(HudLines.SlotIds[i], Ids);
            Assert.Contains(HudLines.RowNames[i], Ids);
            Assert.Contains(HudLines.RowValues[i], Ids);
            Assert.Contains(HudLines.RowToggle[i], Ids);
            Assert.Contains($"Row{i}Up", Ids);
            Assert.Contains($"Row{i}Down", Ids);
        }

        foreach (var name in HudTabs.Names)
        {
            Assert.Contains($"Tab{name}", Ids);
            Assert.Contains($"Tab{name}Label", Ids);
            Assert.Contains($"Page{name}", Ids);
        }

        foreach (var target in HudTargets.All)
        {
            Assert.Contains(HudTargets.Def(target).Panel, Ids);
            Assert.Contains(HudTargets.Def(target).Wrapper, Ids);
        }

        foreach (var button in HudTargets.Buttons.Keys)
        {
            Assert.Contains(button, Ids);
        }

        string[] others =
        [
            "TimerRoot", "Title", "GapTitle", "Time", "TimeCmp", "GapTime", "StageFinish", "StageCmp", "GapStage",
            "CSpeed", "Sr", "Pb", "SplitsBody", "SplitsEmpty", "KeyW", "KeyA", "KeyS", "KeyD", "KeyLeft", "KeyRight",
            "KeyDuck", "KeyGap", "KeyJump", "GuideX", "GuideY", "DragToast", "DragToastWhat", "MoveKeyPlace",
            "MoveKeyCancel", "MoveKeyReset", "MoveKeyFree", "MenuReset", "MenuClose",
        ];

        foreach (var id in others)
        {
            Assert.Contains(id, Ids);
        }

        string[] replayMenu =
        [
            "RMenu", "RmTrackPrev", "RmTrackValue", "RmTrackNext", "RmStagePrev", "RmStageValue", "RmStageNext",
            "RmStylePrev", "RmStyleValue", "RmStyleNext", "RmTabBoard", "RmTabBoardLabel", "RmTabRuns", "RmTabRunsLabel", "RmCount", "RmJumpWr", "RmJumpPb", "RmEmpty", "RmPagePrev", "RmPage", "RmPageNext",
            "RmStatus", "RmNow", "RmNowName", "RmNowTag", "RmNowTime", "RmBarFill", "RmCtl", "RmBack", "RmPause",
            "RmPauseLabel", "RmFwd", "RmSpeed", "RmSpeedLabel", "RmStop", "RmAlong", "RmAlongText", "RmLeave",
            "RmClose", "RmWatch", "RmWatchLabel",
        ];

        string[] profile =
        [
            "PfMenu", "PfName", "PfRank", "PfJoined", "PfStylePrev", "PfStyleValue", "PfStyleNext", "PfOverall",
            "PfOverallTitle", "PfMaps", "PfMapsOf", "PfBonuses", "PfBonusesOf", "PfRecords", "PfHere", "PfPlays",
            "PfTrackPrev", "PfTrackValue", "PfTrackNext", "PfPb", "PfPbRank", "PfStages", "PfClose",
        ];

        foreach (var id in profile.Concat(HudModule.ProfileStageIds).Concat(HudModule.ProfileStageNameIds).Concat(HudModule.ProfileStageTimeIds))
        {
            Assert.Contains(id, Ids);
        }

        foreach (var id in replayMenu.Concat(HudModule.ReplayRowIds).Concat(HudModule.ReplayRankIds).Concat(HudModule.ReplayNameIds)
                                     .Concat(HudModule.ReplayTimeIds).Concat(HudModule.ReplayGapIds))
        {
            Assert.Contains(id, Ids);
        }

        for (var i = 0; i < HudPlayer.MaxSplits; i++)
        {
            Assert.Contains($"Split{i}", Ids);
            Assert.Contains($"Split{i}Name", Ids);
            Assert.Contains($"Split{i}Time", Ids);
            Assert.Contains($"Split{i}Diff", Ids);
        }
    }

    [Fact]
    public void EveryClassThePluginSetsHasACssRule()
    {
        var classes = new List<string>
        {
            "Hidden", "on", "off", "disabled", "active", "placed", "dragging", "editing", "smooth", "moving", "shown",
            "gain", "loss", "gap", "blank", "stopped", "paused", "practice", "replay", "finished", "faster", "slower",
            "nofade", "shift-a", "shift-b", "enter-a", "enter-b", "Closed", "sel", "you", "now", "wr", "warn", "lit", "none",
        };

        // The replay menu's progress bar, in 2% steps.
        classes.AddRange(Enumerable.Range(0, 51).Select(i => $"w-{i}"));

        classes.AddRange(HudOptions.All.SelectMany(o => o.Choices).Select(c => c.Class).OfType<string>());

        var missing = classes.Distinct()
                             .Where(c => !Regex.IsMatch(Css, $@"\.{Regex.Escape(c)}(?![\w-])"))
                             .ToList();

        Assert.Empty(missing);
    }
}

public sealed class HudReplayKeysTests
{
    private sealed class FakeBot : IReplayBotData
    {
        public ReplayBotConfig                Config       { get; init; } = new ();
        public PlayerSlot                     Slot         { get; init; }
        public int                            Track        { get; init; }
        public int                            Style        { get; init; }
        public int                            Stage        { get; init; }
        public float                          Time         { get; init; }
        public ReplayFileHeader?              Header       { get; init; }
        public IReadOnlyList<ReplayFrameData> Frames       { get; init; } = [];
        public int                            CurrentFrame { get; init; }
        public EReplayBotStatus               Status       { get; init; } = EReplayBotStatus.Running;
        public EReplayBotType                 Type         { get; init; }
        public int                            Rank         { get; init; } = 1;
        public long                           RunId        { get; init; }
        public bool                           Paused       { get; init; }
        public float                          Speed        { get; init; } = 1f;
        public PlayerSlot?                    Owner        { get; init; }
    }

    private static ReplayFrameData Frame(float yaw, UserCommandButtons buttons = 0)
        => new () { Angles = new Vector2D(0, yaw), PressedButtons = buttons };

    [Fact]
    public void ShowsTheKeysOfTheFrameLastPlayed()
    {
        // Playback has played frame 1 and moved on to 2.
        var bot = new FakeBot
        {
            Frames       = [Frame(0, UserCommandButtons.Forward), Frame(0, UserCommandButtons.MoveLeft), Frame(0, UserCommandButtons.Jump)],
            CurrentFrame = 2,
        };

        Assert.Equal(UserCommandButtons.MoveLeft, Source2Surf.Timer.Modules.HudModule.ReplayKeys(bot).Buttons);
    }

    [Fact]
    public void TurnsFollowTheRecordedYaw()
    {
        var left  = new FakeBot { Frames = Enumerable.Range(0, 10).Select(i => Frame(i * 2f)).ToArray(), CurrentFrame = 9 };
        var right = new FakeBot { Frames = Enumerable.Range(0, 10).Select(i => Frame(-i * 2f)).ToArray(), CurrentFrame = 9 };
        var still = new FakeBot { Frames = Enumerable.Range(0, 10).Select(_ => Frame(90f)).ToArray(), CurrentFrame = 9 };
        var wraps = new FakeBot { Frames = [Frame(179f), Frame(-179f)], CurrentFrame = 2 };   // across ±180 is a left turn

        Assert.Equal(1, Source2Surf.Timer.Modules.HudModule.ReplayKeys(left).Turn);
        Assert.Equal(-1, Source2Surf.Timer.Modules.HudModule.ReplayKeys(right).Turn);
        Assert.Equal(0, Source2Surf.Timer.Modules.HudModule.ReplayKeys(still).Turn);
        Assert.Equal(1, Source2Surf.Timer.Modules.HudModule.ReplayKeys(wraps).Turn);
    }

    [Theory]
    [InlineData(EReplayBotStatus.Idle)]
    [InlineData(EReplayBotStatus.Start)]
    [InlineData(EReplayBotStatus.End)]
    public void NoKeysUnlessTheReplayIsPlaying(EReplayBotStatus status)
    {
        var bot = new FakeBot { Frames = [Frame(0, UserCommandButtons.Forward)], CurrentFrame = 1, Status = status };

        Assert.Equal((default(UserCommandButtons), 0), Source2Surf.Timer.Modules.HudModule.ReplayKeys(bot));
    }

    [Fact]
    public void TheFirstFrameAndAnEmptyReplayAreSafe()
    {
        Assert.Equal(UserCommandButtons.Duck,
                     Source2Surf.Timer.Modules.HudModule.ReplayKeys(new FakeBot { Frames = [Frame(0, UserCommandButtons.Duck)], CurrentFrame = 0 }).Buttons);
        Assert.Equal((default(UserCommandButtons), 0), Source2Surf.Timer.Modules.HudModule.ReplayKeys(new FakeBot()));
    }
}

// The HUD's texts against the shipped locale file, and the translator's fallbacks.
public sealed class HudLocaleTests
{
    private static readonly Dictionary<string, Dictionary<string, string>> Locale
        = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(HudAssets.Locale()))!;

    private static string Placeholders(string text)
        => string.Join(",", Regex.Matches(text, @"{(d+)}").Select(m => m.Groups[1].Value).Order());

    [Fact]
    public void EveryHudTextIsInTheLocaleFileWithItsEnglish()
    {
        Assert.Equal(HudTexts.All.Count, HudTexts.All.Select(t => t.Key).Distinct().Count());

        foreach (var text in HudTexts.All)
        {
            Assert.True(Locale.TryGetValue(text.Key, out var translations), text.Key);
            Assert.Equal(text.English, translations["en-us"]);
        }

        Assert.Empty(Locale.Keys.Except(HudTexts.All.Select(t => t.Key)));
    }

    [Fact]
    public void EveryTranslationKeepsThePlaceholders()
    {
        foreach (var (key, translations) in Locale)
        {
            foreach (var (language, text) in translations)
            {
                Assert.True(Placeholders(text) == Placeholders(translations["en-us"]), $"{key} ({language})");
            }
        }
    }

    // How Timer.Localization gets templates out of LocalizerManager, which only formats.
    [Fact]
    public void TemplatesFormattedWithTheirOwnPlaceholdersComeBackUnchanged()
    {
        foreach (var translations in Locale.Values)
        {
            foreach (var text in translations.Values)
            {
                Assert.Equal(text, string.Format(System.Globalization.CultureInfo.InvariantCulture, text, "{0}", "{1}", "{2}", "{3}", "{4}", "{5}"));
            }
        }
    }

    [Fact]
    public void EveryFixedLabelIsSetByThePlugin()
    {
        var xml    = File.ReadAllText(HudAssets.Layout());
        var labels = HudLabels.All.ToList();

        Assert.Equal(labels.Count, labels.Select(l => l.Id).Distinct().Count());

        foreach (var (id, _) in labels)
        {
            Assert.Matches($"<Label id=\"{id}\"[^>]* text=\"{Regex.Escape("{s:text}")}\"", xml);
        }

        // Anything else a label shows comes from the plugin, or reads the same in every language.
        string[] same = ["W", "A", "S", "D", "−5s", "+5s"];
        var literal = Regex.Matches(xml, "<Label[^>]* text=\"([^\"]*)\"")
                           .Select(m => Regex.Replace(m.Groups[1].Value, @"{[sg]:[a-z_:]+}", ""))
                           .Where(t => t.Any(char.IsLetter) && !same.Contains(t));

        Assert.Empty(literal);
    }

    // custom_hud refuses the whole layout over one: "Layout contains disallowed attribute html for panel type 'Label'".
    [Fact]
    public void TheLayoutUsesNoHtmlLabels()
        => Assert.DoesNotMatch(@"<Label[^>]* html=", File.ReadAllText(HudAssets.Layout()));

    [Theory]
    [InlineData("of 2 · 50%", false)]
    [InlineData("共 2 · 50%", true)]
    [InlineData("10 次游玩", true)]
    [InlineData("#3 of 12", false)]
    [InlineData("", false)]
    public void TellsNotesInAFallbackScript(string note, bool cjk)
        => Assert.Equal(cjk, HudFormat.HasCjk(note));

    [Theory]
    [InlineData("1", "of 2 · 50%", 0)]         // English: nothing to fix
    [InlineData("1", "共 2 · 50%", 2)]         // a CJK note sits low after a plain number
    [InlineData("2 小时 59 分钟", "13 次游玩", 1)] // and a little low after CJK text
    [InlineData("无", "", 0)]
    public void LiftsACjkNoteOntoItsValue(string value, string note, int lift)
        => Assert.Equal(lift, HudFormat.NoteLift(value, note));

    [Fact]
    public void TheTranslatorFallsBackToEnglish()
    {
        var chinese = new HudTr(key => key == HudTexts.LineSpeed.Key ? "速度：{0}" : null);
        Assert.Equal("速度：25", chinese.Format(HudTexts.LineSpeed, 25));
        Assert.Equal("Sync: 91.80%", chinese.Format(HudTexts.LineSync, "91.80")); // no translation

        var broken = new HudTr(_ => "{3}");
        Assert.Equal("Speed: 25", broken.Format(HudTexts.LineSpeed, 25)); // placeholders that don't fit
        Assert.Equal("Speed: 25", HudTr.English.Format(HudTexts.LineSpeed, 25));
    }
}

internal static class HudAssets
{
    private static readonly string Root = FindRoot();

    public static string Layout()
        => Path.Combine(Root, "panorama", "layout", "custom_game", "surftimer", "hud.xml");

    public static string Style(string file)
        => Path.Combine(Root, "panorama", "styles", "custom_game", "surftimer", file);

    public static string Locale()
        => Path.Combine(Root, "Plugin", "Timer.Localization", "locales", "surftimer.json");

    // The repository root: the first directory up from the test binaries that holds the panorama folder.
    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "panorama", "layout", "custom_game", "surftimer")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("panorama/layout/custom_game/surftimer not found above the test binaries");
    }
}
