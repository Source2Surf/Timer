/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo and Kxnrl
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cysharp.Text;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A text the HUD sets: its key for the localization provider, and its English, which players read when the
///     provider has nothing for them. {0}, {1}… mark where values go.
/// </summary>
internal sealed record HudText(string Key, string English);

/// <summary>
///     The texts one player reads: in their language where the localization provider has them, else English. A
///     translation whose placeholders don't fit falls back to the English instead of failing the refresh.
/// </summary>
internal readonly struct HudTr(Func<string, string?>? lookup)
{
    public static HudTr English => default;

    public string this[HudText text]
        => lookup?.Invoke(text.Key) ?? text.English;

    public string Format<T1>(HudText text, T1 a)
    {
        try
        {
            return ZString.Format(this[text], a);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a);
        }
    }

    public string Format<T1, T2>(HudText text, T1 a, T2 b)
    {
        try
        {
            return ZString.Format(this[text], a, b);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a, b);
        }
    }

    public string Format<T1, T2, T3>(HudText text, T1 a, T2 b, T3 c)
    {
        try
        {
            return ZString.Format(this[text], a, b, c);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a, b, c);
        }
    }
}

/// <summary>
///     Every text the HUD sets, by key. The locale file (Plugin/Timer.Localization/locales/surftimer.json) carries
///     each of them; a test keeps the two in step.
/// </summary>
internal static class HudTexts
{
    // ---- timer titles and lines
    public static readonly HudText MapCompleted   = new ("hud.title.map_completed", "Map Completed!");
    public static readonly HudText BonusCompleted = new ("hud.title.bonus_completed", "Bonus {0} Completed!");
    public static readonly HudText PracticeTitle  = new ("hud.title.practice", "{0} (Practice)");
    public static readonly HudText RunStopped     = new ("hud.title.run_stopped", "Run Stopped");
    public static readonly HudText TimerPaused    = new ("hud.title.timer_paused", "Timer Paused");
    public static readonly HudText PracticeMode   = new ("hud.title.practice_mode", "Practice Mode");
    public static readonly HudText ReplayTitle    = new ("hud.title.replay", "Replay: {0} ({1})");
    public static readonly HudText ReplayIdle     = new ("hud.title.replay_idle", "Replay Bot (Idle)");
    public static readonly HudText ReplayPickHint = new ("hud.replay.pick_hint", "Press E to pick a replay");
    public static readonly HudText ReplayPaused   = new ("hud.replay.paused", "[Paused]");

    public static readonly HudText Time          = new ("hud.time", "Time");
    public static readonly HudText FinalTime     = new ("hud.final_time", "Final Time");
    public static readonly HudText TimeLine      = new ("hud.time_line", "{0}: {1}");
    public static readonly HudText StageFinished = new ("hud.stage_finished", "Finished [Stage {0}]: {1}");
    public static readonly HudText InfoSr        = new ("hud.info.sr", "SR: {0}");
    public static readonly HudText InfoPb        = new ("hud.info.pb", "PB: {0}");

    public static readonly HudText LineZone    = new ("hud.line.zone", "[Zone: {0}]");
    public static readonly HudText LineMode    = new ("hud.line.mode", "Mode: {0}");
    public static readonly HudText LineSpeed   = new ("hud.line.speed", "Speed: {0}");
    public static readonly HudText LineSync    = new ("hud.line.sync", "Sync: {0}%");
    public static readonly HudText LineJumps   = new ("hud.line.jumps", "Jumps: {0}");
    public static readonly HudText LineStrafes = new ("hud.line.strafes", "Strafes: {0}");
    public static readonly HudText LineStart   = new ("hud.line.start", "Start: {0} u/s");
    public static readonly HudText LineEnd     = new ("hud.line.end", "End: {0} u/s");
    public static readonly HudText SpeedCmp    = new ("hud.speed_cmp", " ({0}: {1})");

    public static readonly HudText TagPr = new ("hud.tag.pr", "PR");
    public static readonly HudText TagPb = new ("hud.tag.pb", "PB");
    public static readonly HudText TagSr = new ("hud.tag.sr", "SR");

    // ---- zones
    public static readonly HudText ZoneMapStart   = new ("hud.zone.map_start", "Map Start Zone");
    public static readonly HudText ZoneBonusStart = new ("hud.zone.bonus_start", "Bonus {0} Start Zone");
    public static readonly HudText ZoneStageStart = new ("hud.zone.stage_start", "Stage {0} Start Zone");
    public static readonly HudText ZoneMapEnd     = new ("hud.zone.map_end", "Map End Zone");
    public static readonly HudText ZoneBonusEnd   = new ("hud.zone.bonus_end", "Bonus {0} End Zone");
    public static readonly HudText ZoneNone       = new ("hud.zone.none", "None");
    public static readonly HudText ZoneLinear     = new ("hud.zone.linear", "Linear");

    // ---- shared words
    public static readonly HudText NotAvailable = new ("hud.na", "N/A");
    public static readonly HudText Main         = new ("hud.track.main", "Main");
    public static readonly HudText BonusN       = new ("hud.track.bonus", "Bonus {0}");
    public static readonly HudText FullRun      = new ("hud.stage.full", "Full run");
    public static readonly HudText StageN       = new ("hud.stage.n", "Stage {0}");
    public static readonly HudText CheckpointN  = new ("hud.checkpoint.n", "CP {0}");
    public static readonly HudText Versus       = new ("hud.split.vs", "{0} vs {1}");
    public static readonly HudText Sr           = new ("hud.sr", "SR");
    public static readonly HudText Pb           = new ("hud.pb", "PB");
    public static readonly HudText Run          = new ("hud.run", "Run");
    public static readonly HudText Rank         = new ("hud.rank", "#{0}");
    public static readonly HudText RankOf       = new ("hud.rank_of", "#{0} of {1}");
    public static readonly HudText Loading      = new ("hud.loading", "Loading…");

    // ---- menu values and the Timer tab's lines
    public static readonly HudText ChoiceOn           = new ("hud.choice.on", "On");
    public static readonly HudText ChoiceOff          = new ("hud.choice.off", "Off");
    public static readonly HudText ChoicePersonalBest = new ("hud.choice.personal_best", "Personal best");
    public static readonly HudText ChoiceServerRecord = new ("hud.choice.server_record", "Server record");
    public static readonly HudText ChoiceHorizontal   = new ("hud.choice.horizontal", "Horizontal");

    public static readonly HudText RowZone    = new ("hud.row.zone", "Zone");
    public static readonly HudText RowBlank   = new ("hud.row.blank", "Blank line");
    public static readonly HudText RowMode    = new ("hud.row.mode", "Mode");
    public static readonly HudText RowSpeed   = new ("hud.row.speed", "Speed");
    public static readonly HudText RowStart   = new ("hud.row.start", "Start / end speed");
    public static readonly HudText RowSync    = new ("hud.row.sync", "Sync");
    public static readonly HudText RowJumps   = new ("hud.row.jumps", "Jumps");
    public static readonly HudText RowStrafes = new ("hud.row.strafes", "Strafes");

    // ---- replay menu
    public static readonly HudText TimesOne        = new ("rm.count.times_one", "1 time");
    public static readonly HudText Times           = new ("rm.count.times", "{0} times");
    public static readonly HudText RunsOne         = new ("rm.count.runs_one", "1 run");
    public static readonly HudText Runs            = new ("rm.count.runs", "{0} runs");
    public static readonly HudText NoTimes         = new ("rm.empty.times", "No times on {0} yet.");
    public static readonly HudText RunsLoading     = new ("rm.empty.runs_loading", "Loading your runs…");
    public static readonly HudText NoRuns          = new ("rm.empty.runs", "You have no runs here yet.");
    public static readonly HudText Page            = new ("rm.page", "{0} / {1}");
    public static readonly HudText Playing         = new ("rm.playing", "PLAYING");
    public static readonly HudText You             = new ("rm.you", "You");
    public static readonly HudText AgoNow          = new ("rm.ago.now", "Just now");
    public static readonly HudText AgoMinutes      = new ("rm.ago.minutes", "{0} min ago");
    public static readonly HudText AgoHours        = new ("rm.ago.hours", "{0} h ago");
    public static readonly HudText AgoDay          = new ("rm.ago.day", "1 day ago");
    public static readonly HudText AgoDays         = new ("rm.ago.days", "{0} days ago");
    public static readonly HudText WhoseSr         = new ("rm.whose.sr", "{0}'s SR");
    public static readonly HudText WhoseRank       = new ("rm.whose.rank", "{0}'s #{1}");
    public static readonly HudText WhoseRun        = new ("rm.whose.run", "{0}'s run");
    public static readonly HudText YourRun         = new ("rm.whose.yours", "your run");
    public static readonly HudText YourTimedRun    = new ("rm.whose.yours_timed", "your {0} run");
    public static readonly HudText NoBot           = new ("rm.status.no_bot", "This server has no replay bot for !replay.");
    public static readonly HudText LoadingWhat     = new ("rm.status.loading", "Loading {0}…");
    public static readonly HudText Busy            = new ("rm.status.busy", "The replay bot is busy: {0} is watching {1}. You can watch along, or wait until they stop.");
    public static readonly HudText Someone         = new ("rm.status.someone", "someone");
    public static readonly HudText BotFree         = new ("rm.status.free", "The replay bot is free. Pick a time to watch, or join a team to play.");
    public static readonly HudText MenuHint        = new ("rm.status.hint", "Pick a time and press Watch. It plays once on the replay bot and you spectate it; Stop puts you back at the start.");
    public static readonly HudText NoReplay        = new ("rm.note.no_replay", "No replay saved for {0}.");
    public static readonly HudText StartedFirst    = new ("rm.note.busy", "Someone else started a replay first. You can watch along.");
    public static readonly HudText AlongOwner      = new ("rm.along.owner", "{0} started this replay, so they have the controls.");
    public static readonly HudText AlongLeft       = new ("rm.along.left", "Whoever started this replay left. Pick a time to play your own.");
    public static readonly HudText NoBotButton     = new ("rm.button.no_bot", "No replay bot");
    public static readonly HudText WatchingAlong   = new ("rm.button.watching_along", "Watching along");
    public static readonly HudText WatchAlong      = new ("rm.button.watch_along", "Watch along");
    public static readonly HudText Watch           = new ("rm.button.watch", "Watch");
    public static readonly HudText PlayingButton   = new ("rm.button.playing", "Playing");
    public static readonly HudText PlayWhat        = new ("rm.button.play_what", "Play {0}");
    public static readonly HudText WatchWhat       = new ("rm.button.watch_what", "Watch {0}");
    public static readonly HudText Pause           = new ("rm.button.pause", "Pause");
    public static readonly HudText Play            = new ("rm.button.play", "Play");

    // ---- profile card
    public static readonly HudText ProfileRank    = new ("pf.rank", "#{0} of {1} · {2} points");
    public static readonly HudText ProfilePoints  = new ("pf.points", "{0} points");
    public static readonly HudText Joined         = new ("pf.joined", "Joined {0}");
    public static readonly HudText Played         = new ("pf.played", " · Played {0}");
    public static readonly HudText Overall        = new ("pf.overall", "Overall · {0}");
    public static readonly HudText OfTotal        = new ("pf.of_total", "of {0} · {1}%");
    public static readonly HudText OfTotalNone    = new ("pf.of_total_none", "of {0}");
    public static readonly HudText RecordsHeld    = new ("pf.records", "{0} maps · {1} bonuses · {2} stages");
    public static readonly HudText PlaysOne       = new ("pf.plays_one", "{0} play");
    public static readonly HudText Plays          = new ("pf.plays", "{0} plays");
    public static readonly HudText None           = new ("pf.none", "None");
    public static readonly HudText StageTile      = new ("pf.stage_tile", "STAGE {0}");
    public static readonly HudText Minutes        = new ("pf.duration.minutes", "{0} min");
    public static readonly HudText HoursMinutes   = new ("pf.duration.hours_minutes", "{0} h {1} min");
    public static readonly HudText Hours          = new ("pf.duration.hours", "{0} h");

    // ---- the layout's fixed labels (HudLabels)
    public static readonly HudText Close        = new ("ui.close", "Close");
    public static readonly HudText Show         = new ("ui.show", "Show");
    public static readonly HudText Track        = new ("ui.track", "Track");
    public static readonly HudText Stage        = new ("ui.stage", "Stage");
    public static readonly HudText Style        = new ("ui.style", "Style");
    public static readonly HudText SplitsEmpty  = new ("ui.splits.empty", "Splits show here");
    public static readonly HudText KeyDuck      = new ("ui.keys.duck", "DUCK");
    public static readonly HudText KeyJump      = new ("ui.keys.jump", "JUMP");

    public static readonly HudText DragMoving   = new ("ui.drag.moving", "Moving {0}");
    public static readonly HudText DragPlace    = new ("ui.drag.place", "Place");
    public static readonly HudText DragCancel   = new ("ui.drag.cancel", "Cancel");
    public static readonly HudText DragReset    = new ("ui.drag.reset", "Default spot");
    public static readonly HudText DragFree     = new ("ui.drag.free", "Hold for free move");
    public static readonly HudText TargetMenu   = new ("ui.target.menu", "menu");
    public static readonly HudText TargetRun    = new ("ui.target.timer", "timer");
    public static readonly HudText TargetCSpeed = new ("ui.target.cspeed", "center speed");
    public static readonly HudText TargetInfo   = new ("ui.target.records", "records");
    public static readonly HudText TargetSplits = new ("ui.target.splits", "splits");
    public static readonly HudText TargetKeys   = new ("ui.target.keys", "keys");
    public static readonly HudText TargetLocs   = new ("ui.target.locs", "locations");

    public static readonly HudText LocsTitle      = new ("ui.locs.title", "Locations");
    public static readonly HudText LocsTitleCount = new ("ui.locs.title_count", "Locations · {0}");
    public static readonly HudText LocsSave       = new ("ui.locs.save", "Save");
    public static readonly HudText LocsTeleport   = new ("ui.locs.teleport", "Teleport");
    public static readonly HudText LocsTeleportTo = new ("ui.locs.teleport_to", "Teleport #{0}");
    public static readonly HudText LocsPrev       = new ("ui.locs.prev", "Previous");
    public static readonly HudText LocsNext       = new ("ui.locs.next", "Next");
    public static readonly HudText LocsHide       = new ("ui.locs.hide", "Hide");
    public static readonly HudText LocsClear      = new ("ui.locs.clear", "Clear all");
    public static readonly HudText LocsClearAsk   = new ("ui.locs.clear_ask", "Press again to clear all {0}");
    public static readonly HudText LocsNote       = new ("ui.locs.note", "Bind keys in the console, like: bind mouse4 saveloc (also loc, prevloc, nextloc, clearloc). Hide and show this panel to update the keys.");
    public static readonly HudText LocsNoteFirst  = new ("ui.locs.note_first", "Bind keys in the console, like: bind mouse4 saveloc (also loc, prevloc, nextloc, clearloc). Hide and show this panel to update the keys. Saving puts your run in practice.");
    public static readonly HudText LocsNoteFirstSegmented = new ("ui.locs.note_first_segmented", "Bind keys in the console, like: bind mouse4 saveloc (also loc, prevloc, nextloc, clearloc). Hide and show this panel to update the keys. On this segmented style, saving keeps your run record-eligible.");

    public static readonly HudText MenuTitle      = new ("ui.menu.title", "HUD settings");
    public static readonly HudText MenuMove       = new ("ui.menu.move", "Move");
    public static readonly HudText MenuReset      = new ("ui.menu.reset", "Reset");
    public static readonly HudText TabHud         = new ("ui.tab.hud", "HUD");
    public static readonly HudText TabTimer       = new ("ui.tab.timer", "Timer");
    public static readonly HudText TabSpeed       = new ("ui.tab.speed", "Speed");
    public static readonly HudText TabSplits      = new ("ui.tab.splits", "Splits");
    public static readonly HudText TabKeys        = new ("ui.tab.keys", "Keys");
    public static readonly HudText Panels         = new ("ui.panels", "Panels");
    public static readonly HudText PanelRun       = new ("ui.panel.timer", "Timer");
    public static readonly HudText PanelCSpeed    = new ("ui.panel.cspeed", "Center speed");
    public static readonly HudText PanelInfo      = new ("ui.panel.records", "Records");
    public static readonly HudText PanelInfoDesc  = new ("ui.panel.records_desc", "Server record and your PB");
    public static readonly HudText PanelSplits    = new ("ui.panel.splits", "Splits");
    public static readonly HudText PanelKeys      = new ("ui.panel.keys", "Keys");
    public static readonly HudText PanelKeysDesc  = new ("ui.panel.keys_desc", "Also !showkeys");
    public static readonly HudText Lines          = new ("ui.timer.lines", "Lines, top to bottom");
    public static readonly HudText Comparison     = new ("ui.timer.comparison", "Comparison");
    public static readonly HudText CompareAgainst = new ("ui.timer.compare", "Compare against");
    public static readonly HudText Live           = new ("ui.timer.live", "Live difference");
    public static readonly HudText LiveDesc       = new ("ui.timer.live_desc", "As you move, against your PB or the SR replay");
    public static readonly HudText SpeedSection   = new ("ui.speed.section", "Timer and center speed");
    public static readonly HudText SpeedColor     = new ("ui.speed.colors", "Gain / loss colours");
    public static readonly HudText SpeedColorDesc = new ("ui.speed.colors_desc", "On center speed");
    public static readonly HudText SpeedAxes      = new ("ui.speed.measure", "Measure");
    public static readonly HudText SpeedAxesDesc  = new ("ui.speed.measure_desc", "3D includes vertical speed");
    public static readonly HudText SplitRows      = new ("ui.splits.recent", "Recent splits");
    public static readonly HudText SplitFade      = new ("ui.splits.fade", "Fade older splits");
    public static readonly HudText SplitTip       = new ("ui.splits.tip", "Differences follow Compare against on the Timer tab. Stages on staged maps, checkpoints on linear ones.");
    public static readonly HudText KeyMouse       = new ("ui.keys.turn", "Turn direction");
    public static readonly HudText KeyMouseDesc   = new ("ui.keys.turn_desc", "Arrows light up as you turn");
    public static readonly HudText KeyJumpDuck    = new ("ui.keys.jump_duck", "Jump and duck");

    public static readonly HudText ReplaysTitle = new ("ui.replay.title", "Replays");
    public static readonly HudText Leaderboard  = new ("ui.replay.leaderboard", "Leaderboard");
    public static readonly HudText MyRuns       = new ("ui.replay.my_runs", "My runs");
    public static readonly HudText YourPb       = new ("ui.replay.your_pb", "Your PB");
    public static readonly HudText Stop         = new ("ui.replay.stop", "Stop");
    public static readonly HudText Leave        = new ("ui.replay.leave", "Leave");

    public static readonly HudText ProfileTitle = new ("ui.profile.title", "Profile");
    public static readonly HudText MapsDone     = new ("ui.profile.maps", "Maps completed");
    public static readonly HudText BonusesDone  = new ("ui.profile.bonuses", "Bonuses completed");
    public static readonly HudText ServerRecords = new ("ui.profile.records", "Server records");
    public static readonly HudText ThisMap      = new ("ui.profile.this_map", "This map");
    public static readonly HudText TimeHere     = new ("ui.profile.time_here", "Time here");
    public static readonly HudText PersonalBest = new ("ui.profile.pb", "Personal best");

    /// <summary>
    ///     All of them, for the test that checks the locale file.
    /// </summary>
    public static IReadOnlyList<HudText> All { get; } = typeof(HudTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                        .Where(f => f.FieldType == typeof(HudText))
                                                                        .Select(f => (HudText) f.GetValue(null)!)
                                                                        .ToList();
}

/// <summary>
///     The layout's fixed labels by panel: each Label's id, whose {s:text} is set to the player's text while the
///     panel shows. Unchanged texts aren't resent, so they cost nothing after the first time.
/// </summary>
internal static class HudLabels
{
    public static readonly (string Id, HudText Text)[] Menu =
    [
        ("LMenuTitle", HudTexts.MenuTitle),
        ("LMenuGrip", HudTexts.MenuMove),
        ("TabHudLabel", HudTexts.TabHud),
        ("TabTimerLabel", HudTexts.TabTimer),
        ("TabSpeedLabel", HudTexts.TabSpeed),
        ("TabSplitsLabel", HudTexts.TabSplits),
        ("TabKeysLabel", HudTexts.TabKeys),
        ("LPanels", HudTexts.Panels),
        ("LPanelRun", HudTexts.PanelRun),
        ("LPanelCSpeed", HudTexts.PanelCSpeed),
        ("LPanelInfo", HudTexts.PanelInfo),
        ("LPanelInfoDesc", HudTexts.PanelInfoDesc),
        ("LPanelSplits", HudTexts.PanelSplits),
        ("LPanelKeys", HudTexts.PanelKeys),
        ("LPanelKeysDesc", HudTexts.PanelKeysDesc),
        ("LLines", HudTexts.Lines),
        ("LComparison", HudTexts.Comparison),
        ("LCompare", HudTexts.CompareAgainst),
        ("LLive", HudTexts.Live),
        ("LLiveDesc", HudTexts.LiveDesc),
        ("LSpeedSection", HudTexts.SpeedSection),
        ("LSpeedColor", HudTexts.SpeedColor),
        ("LSpeedColorDesc", HudTexts.SpeedColorDesc),
        ("LSpeedAxes", HudTexts.SpeedAxes),
        ("LSpeedAxesDesc", HudTexts.SpeedAxesDesc),
        ("LSplitsShow", HudTexts.Show),
        ("LSplitRows", HudTexts.SplitRows),
        ("LSplitFade", HudTexts.SplitFade),
        ("LSplitTip", HudTexts.SplitTip),
        ("LKeysShow", HudTexts.Show),
        ("LKeyMouse", HudTexts.KeyMouse),
        ("LKeyMouseDesc", HudTexts.KeyMouseDesc),
        ("LKeyJumpDuck", HudTexts.KeyJumpDuck),
        ("LMenuReset", HudTexts.MenuReset),
        ("LMenuClose", HudTexts.Close),
    ];

    public static readonly (string Id, HudText Text)[] DragToast =
    [
        ("LToastPlace", HudTexts.DragPlace),
        ("LToastCancel", HudTexts.DragCancel),
        ("LToastReset", HudTexts.DragReset),
        ("LToastFree", HudTexts.DragFree),
    ];

    public static readonly (string Id, HudText Text)[] Replays =
    [
        ("LRmTitle", HudTexts.ReplaysTitle),
        ("LRmTrack", HudTexts.Track),
        ("LRmStage", HudTexts.Stage),
        ("LRmStyle", HudTexts.Style),
        ("RmTabBoardLabel", HudTexts.Leaderboard),
        ("RmTabRunsLabel", HudTexts.MyRuns),
        ("LRmJumpWr", HudTexts.Sr),
        ("LRmJumpPb", HudTexts.YourPb),
        ("LRmStop", HudTexts.Stop),
        ("LRmLeave", HudTexts.Leave),
        ("LRmClose", HudTexts.Close),
    ];

    public static readonly (string Id, HudText Text)[] Profile =
    [
        ("LPfTitle", HudTexts.ProfileTitle),
        ("LPfStyle", HudTexts.Style),
        ("LPfMaps", HudTexts.MapsDone),
        ("LPfBonuses", HudTexts.BonusesDone),
        ("LPfRecords", HudTexts.ServerRecords),
        ("LPfThisMap", HudTexts.ThisMap),
        ("LPfHere", HudTexts.TimeHere),
        ("LPfTrack", HudTexts.Track),
        ("LPfPb", HudTexts.PersonalBest),
        ("LPfClose", HudTexts.Close),
    ];

    public static readonly (string Id, HudText Text)[] Keys =
    [
        ("KeyDuck", HudTexts.KeyDuck),
        ("KeyJump", HudTexts.KeyJump),
    ];

    public static IEnumerable<(string Id, HudText Text)> All
        => Menu.Concat(DragToast).Concat(Replays).Concat(Profile).Concat(Keys).Append(("SplitsEmpty", HudTexts.SplitsEmpty));
}
