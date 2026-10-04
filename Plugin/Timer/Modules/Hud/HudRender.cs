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
using System.Globalization;
using Cysharp.Text;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Timer;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

internal partial class HudModule
{
    /// <summary>
    ///     Writes to one player's layout, each value and class only when it changes. A fresh entity gets every class
    ///     once, so the layout's own defaults don't need mirroring here. Classes are always set explicitly: handing
    ///     one back to the default leaves the client as it is.
    /// </summary>
    private readonly struct HudWriter(ICustomHudLayout layout, HudPlayer p)
    {
        public void Text(string panel, string variable, string value)
        {
            var key = (panel, variable);

            if (p.SentText.TryGetValue(key, out var current) && current == value)
            {
                return;
            }

            p.SentText[key] = value;
            layout.SetDialogVariableString(panel, variable, AsShown(variable, value));
        }

        // The client looks a whole value up in the game's own strings and shows what it finds instead: "Normal" became
        // 普通, "Style" became SET STYLE (valve_english.txt). A zero-width space in front keeps every value as it is,
        // except editkey / editkey2, which name the commands whose bound keys {g:csgo_key:editkey} shows.
        private static string AsShown(string variable, string value)
            => value.Length == 0 || variable.StartsWith("editkey", StringComparison.Ordinal) ? value : ZString.Concat('\u200B', value);

        // Fixed labels, in the player's language.
        public void Labels((string Id, HudText Text)[] labels)
        {
            foreach (var (id, text) in labels)
            {
                Text(id, "text", p.Tr[text]);
            }
        }

        public void Class(string panel, string className, bool on)
        {
            var key = (panel, className);

            if (p.SentClass.TryGetValue(key, out var current) && current == on)
            {
                return;
            }

            p.SentClass[key] = on;
            layout.SetClassOverride(panel, className, on ? HudPanelClassStatus.ForceEnable : HudPanelClassStatus.ForceDisable);
        }

        /// <summary>
        ///     Moves a panel from class prefix-old to prefix-<paramref name="n" />; null just removes it.
        /// </summary>
        public void Numbered(string panel, string prefix, string? n)
        {
            var key = (panel, prefix);
            p.SentNumbered.TryGetValue(key, out var current);

            if (current == n)
            {
                return;
            }

            if (current is not null)
            {
                layout.SetClassOverride(panel, ZString.Concat(prefix, '-', current), HudPanelClassStatus.ForceDisable);
            }

            if (n is null)
            {
                p.SentNumbered.Remove(key);
            }
            else
            {
                layout.SetClassOverride(panel, ZString.Concat(prefix, '-', n), HudPanelClassStatus.ForceEnable);
                p.SentNumbered[key] = n;
            }
        }
    }

    /// <summary>
    ///     Whose run the HUD shows: the player's own, or while spectating, the player or replay bot they watch.
    /// </summary>
    private readonly record struct HudSource(
        PlayerSlot      Slot,
        IBasePlayerPawn Pawn,
        ITimerInfo?     Timer,
        HudPlayer?      Run,
        IReplayBotData? Replay);

    private HudSource? ResolveSource(HudPlayer p, IPlayerController controller)
    {
        if (controller.GetPawn() is not { } pawn)
        {
            return null;
        }

        if (pawn.AsObserver() is { } observer && observer.GetObserverService() is { } observerService)
        {
            if (observerService.ObserverMode is ObserverMode.None or ObserverMode.Roaming
                || !observerService.ObserverTarget.IsValid()
                || _bridge.EntityManager.FindEntityByHandle(observerService.ObserverTarget)?.AsPlayerPawn() is not { } targetPawn
                || targetPawn.GetController() is not { } target)
            {
                return null;
            }

            if (target.IsFakeClient)
            {
                return _replayModule.GetReplayBotData(target.PlayerSlot) is { } bot
                    ? new HudSource(target.PlayerSlot, targetPawn, null, null, bot)
                    : null;
            }

            return _timerModule.GetTimerInfo(target.PlayerSlot) is { } targetInfo
                ? new HudSource(target.PlayerSlot, targetPawn, targetInfo, _players[target.PlayerSlot], null)
                : null;
        }

        return _timerModule.GetTimerInfo(p.Slot) is { } info ? new HudSource(p.Slot, pawn, info, p, null) : null;
    }

    private void UpdateHud(HudPlayer p, ICustomHudLayout layout, IPlayerController controller, float now)
    {
        var w = new HudWriter(layout, p);

        // The menu, settings and positions apply even while spectating, and only change after a click, a command or
        // a drag, so they're only rebuilt then rather than on every refresh.
        if (p.MenuDirty || (now < p.MenuBusyUntil && p.MenuBusyUntil - now <= 1f))
        {
            p.MenuDirty = false;
            UpdateMenu(w, p, now);
        }

        if (p.Replays.Open)
        {
            UpdateReplayMenu(w, p, controller);
        }

        if (p.Profile.Open)
        {
            UpdateProfile(w, p);
        }

        UpdateMapChooser(w, p, now);
        UpdateZones(w, p);

        var source = ResolveSource(p, controller);
        w.Class("TimerRoot", "Hidden", source is null);

        if (source is not { } s)
        {
            return;
        }

        var speed = MeasureSpeed(p, s.Pawn);

        if (p.IsOn(HudOptions.Run))
        {
            if (s.Replay is { } bot)
            {
                UpdateReplayTimer(w, p, bot, speed);
            }
            else
            {
                UpdateTimer(w, p, s, speed, now);
            }
        }

        if (p.IsOn(HudOptions.CSpeed))
        {
            UpdateCenterSpeed(w, p, speed);
        }

        if (p.IsOn(HudOptions.Info))
        {
            UpdateRecords(w, p, s);
        }

        if (p.IsOn(HudOptions.Splits))
        {
            UpdateSplits(w, p, s);
        }

        if (p.IsOn(HudOptions.Keys))
        {
            UpdateKeys(w, p, s);
        }

        if (p.IsOn(HudOptions.Ssj))
        {
            UpdateSsj(w, p, s, now);
        }

        UpdateLocs(w, p, s);
    }

    // ------------------------------------------------------------------ saved locations

    /// <summary>
    ///     The saved-locations panel: the practice actions with the key the player bound to each. Shown with walk +
    ///     inspect, or while the menu is open so it can be dragged; only on the player's own run.
    /// </summary>
    private void UpdateLocs(HudWriter w, HudPlayer p, HudSource s)
    {
        var shown = (p.LocsShown || p.MenuOpen) && s.Replay is null && s.Slot == p.Slot;
        w.Class("LocsPanel", "Hidden", !shown);

        // The client only looks a key up when a label's text changes, so each time the panel shows, its key caps get a
        // new (invisible) nonce: a key bound while it was hidden shows up.
        if (shown && !p.LocsWasShown)
        {
            p.LocsRecheck = !p.LocsRecheck;
        }

        p.LocsWasShown = shown;

        if (!shown || _bridge.ClientManager.GetGameClient(p.Slot) is not { } client)
        {
            return;
        }

        var tr    = p.Tr;
        var count = _practiceModule.GetLocCount(p.Slot);

        w.Text("LocsTitle", "text", count > 0 ? tr.Format(HudTexts.LocsTitleCount, count) : tr[HudTexts.LocsTitle]);
        w.Text("LocsSave", "text", tr[HudTexts.LocsSave]);
        w.Text("LocsTele", "text", count > 0 ? tr.Format(HudTexts.LocsTeleportTo, _practiceModule.GetCurrentLoc(client) + 1) : tr[HudTexts.LocsTeleport]);
        w.Class("LocsTele", "dim", count == 0);
        w.Text("LocsPrev", "text", tr[HudTexts.LocsPrev]);
        w.Class("LocsPrev", "dim", count < 2);
        w.Text("LocsNext", "text", tr[HudTexts.LocsNext]);
        w.Class("LocsNext", "dim", count < 2);
        w.Text("LocsHide", "text", tr[HudTexts.LocsHide]);

        // Clear all takes a second press within a few seconds; the practice module keeps the clock, and this panel
        // drops the prompt once it says the request has lapsed.
        var asking = count > 0 && _practiceModule.IsClearPending(p.Slot);
        w.Text("LocsClear", "text", asking ? tr.Format(HudTexts.LocsClearAsk, count) : tr[HudTexts.LocsClear]);
        w.Class("LocsClear", "dim", count == 0);
        w.Class("LocsClear", "warn", asking);
        w.Class("LocsKeyClear", "warn", asking);

        // How to bind, and how to see a new bind; before the first save, also what saving does on this style.
        var note = count > 0 ? HudTexts.LocsNote
            : _practiceModule.IsOnSegmentedStyle(p.Slot) ? HudTexts.LocsNoteFirstSegmented
                                                         : HudTexts.LocsNoteFirst;
        w.Text("LocsNote", "text", tr[note]);

        // The command each key cap shows the key of (the practice module's console commands); the client fills in the
        // bound key, or NOT BOUND.
        w.Text("LocsKeySave", "editkey", "%saveloc%");
        w.Text("LocsKeyTele", "editkey", "%loc%");
        w.Text("LocsKeyPrev", "editkey", "%prevloc%");
        w.Text("LocsKeyNext", "editkey", "%nextloc%");
        w.Text("LocsKeyClear", "editkey", "%clearloc%");
        w.Text("LocsKeyHide", "editkey", "%sprint%");
        w.Text("LocsKeyHide", "editkey2", "%lookatweapon%");

        var nonce = p.LocsRecheck ? LocsNonce : "";

        foreach (var cap in LocsCaps)
        {
            w.Text(cap, "nonce", nonce);
        }
    }

    private static readonly string[] LocsCaps = ["LocsKeySave", "LocsKeyTele", "LocsKeyPrev", "LocsKeyNext", "LocsKeyClear", "LocsKeyHide"];

    private const string LocsNonce = "\u200B"; // invisible; any change makes the client redo the label

    // ------------------------------------------------------------------ menu

    private static void UpdateMenu(HudWriter w, HudPlayer p, float now)
    {
        w.Class("Menu", "Closed", !p.MenuOpen);
        w.Class("RMenu", "Closed", !p.Replays.Open);
        w.Class("PfMenu", "Closed", !p.Profile.Open);
        w.Class("Menu", "moving", p.Drag?.Target == HudTarget.Menu);
        w.Class("DragToast", "shown", p.Drag is not null);

        if (p.MenuOpen)
        {
            w.Labels(HudLabels.Menu);
        }

        if (p.Drag is { } drag)
        {
            w.Text("DragToastWhat", "target", p.Tr.Format(HudTexts.DragMoving, p.Tr[HudTargets.Def(drag.Target).Name]));
            w.Labels(HudLabels.DragToast);

            // CJK text comes from a fallback font whose taller line box centres it about a pixel below the key caps'
            // text; lift it back (hud.css .DragToast .lift-1).
            var cjk = HudFormat.HasCjk(p.Tr[HudTexts.DragPlace]);
            w.Class("DragToastWhat", "lift-1", cjk);

            foreach (var (id, _) in HudLabels.DragToast)
            {
                w.Class(id, "lift-1", cjk);
            }
        }

        // {g:csgo_key:editkey} shows each player the key they bound to the command.
        w.Text("MoveKeyPlace", "editkey", "%attack%");
        w.Text("MoveKeyCancel", "editkey", "%attack2%");
        w.Text("MoveKeyReset", "editkey", "%reload%");
        w.Text("MoveKeyFree", "editkey", "%sprint%");

        for (var i = 0; i < HudTabs.Names.Length; i++)
        {
            var active = p.Tab == i;
            w.Class(HudTabs.Tabs[i], "active", active);
            w.Class(HudTabs.Labels[i], "active", active);
            w.Class(HudTabs.Pages[i], "Hidden", !active);
        }

        foreach (var target in HudTargets.All)
        {
            var t = (int) target;

            if (!float.IsNaN(p.UnplaceAt[t]) && (now >= p.UnplaceAt[t] || p.UnplaceAt[t] - now > 1f))
            {
                p.UnplaceAt[t] = float.NaN;
                p.Positions[t] = null;
            }
        }

        foreach (var target in HudTargets.All)
        {
            var def      = HudTargets.Def(target);
            var dragging = p.Drag?.Target == target;
            w.Class(def.Panel, "placed", p.Positions[(int) target] is not null);

            // The menu shows its drag with "moving" and is never "editing".
            if (target != HudTarget.Menu)
            {
                w.Class(def.Panel, "dragging", dragging);
                w.Class(def.Panel, "editing", p.MenuOpen);
            }

            var until = p.SmoothUntil[(int) target];
            SetSmooth(w, target, dragging ? p.Drag!.Smooth : until > now && until - now <= 1f);
            ApplyPosition(w, p, target);
        }

        var dragged = p.Drag is { } d ? p.Positions[(int) d.Target] : null;
        w.Class("GuideX", "shown", dragged?.Cx == true);
        w.Class("GuideY", "shown", dragged?.Cy == true);

        // Timer tab rows: row i edits the line in slot i.
        for (var i = 0; i < HudLines.Count; i++)
        {
            var line   = p.Order[i];
            var option = HudLines.Option(line);

            w.Text(HudLines.RowNames[i], "name", p.Tr[HudLines.Name(line)]);
            w.Class(HudLines.RowNames[i], "blank", option is null);
            w.Class(HudLines.RowToggle[i], "blank", option is null);

            if (option is null)
            {
                continue; // the blank line's pill isn't shown, so whatever it holds can stay
            }

            var choice = option.Choices[p.Settings[option.Index]];

            w.Text(HudLines.RowValues[i], "value", ChoiceText(p.Tr, choice));
            w.Class(HudLines.RowValues[i], "on", choice.Tone == HudTone.On);
            w.Class(HudLines.RowValues[i], "off", choice.Tone == HudTone.Off);
        }

        foreach (var option in HudOptions.All)
        {
            var shown = HudOptions.Display(option, p.Settings);
            var chosen = option.Choices[p.Settings[option.Index]];

            if (!HudLines.RowOptions.Contains(option))
            {
                w.Text(option.ValueId, "value", ChoiceText(p.Tr, shown.Choice));
                w.Class(option.ValueId, "on", shown.Choice.Tone == HudTone.On);
                w.Class(option.ValueId, "off", shown.Choice.Tone == HudTone.Off);

                if (option.Needs is not null)
                {
                    w.Class(option.Id, "disabled", shown.Disabled);
                }
            }

            if (option.IsStepper)
            {
                var index = p.Settings[option.Index];
                w.Class(option.DownId, "disabled", index == 0);
                w.Class(option.UpId, "disabled", index == option.Choices.Length - 1);
            }

            foreach (var panel in option.Panels)
            {
                foreach (var choice in option.Choices)
                {
                    if (choice.Class is not null)
                    {
                        w.Class(panel, choice.Class, choice == chosen);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ speed

    /// <summary>
    ///     Speed as the Measure setting counts it, and whether it's clearly rising or falling.
    /// </summary>
    private static float MeasureSpeed(HudPlayer p, IBasePlayerPawn pawn)
    {
        var velocity = pawn.GetAbsVelocity();
        var speed    = p.Setting(HudOptions.SpeedAxes) == "3D" ? velocity.Length() : velocity.Length2D();

        if (!float.IsFinite(speed))
        {
            speed = 0;
        }

        p.SpeedTrend = HudFormat.SpeedTrend(p.LastSpeed, speed);
        p.LastSpeed  = speed;

        return speed;
    }

    private static void UpdateCenterSpeed(HudWriter w, HudPlayer p, float speed)
    {
        var colors = p.IsOn(HudOptions.SpeedColor);
        w.Text("CSpeed", "speed", HudFormat.RoundSpeed(speed).ToString(CultureInfo.InvariantCulture));
        w.Class("CSpeed", "gain", colors && p.SpeedTrend > 0);
        w.Class("CSpeed", "loss", colors && p.SpeedTrend < 0);
    }

    // ------------------------------------------------------------------ timer

    /// <summary>
    ///     What the timer shows, worked out per state before anything is written.
    /// </summary>
    private sealed class TimerView
    {
        public string? Title;
        public string? TitleClass; // stopped / paused / practice / replay
        public string? TimeLabel;
        public string? Time;
        public bool    Finished;
        public string? TimeCmp;
        public int     TimeCmpSign;
        public int?    Stage;
        public string? StageTime;
        public string? StageCmp;
        public int     StageCmpSign;

        public readonly bool[]    Shown = new bool[HudLines.Count]; // by line
        public readonly string?[] Texts = new string?[HudLines.Count];
    }

    /// <summary>
    ///     One layout per state: start zone (zone, mode, speed, sync), running (time and its comparison, zone,
    ///     speeds, sync), finished (the summary) and stopped (why, then as in a zone).
    /// </summary>
    private void UpdateTimer(HudWriter w, HudPlayer p, HudSource s, float speed, float now)
    {
        var info     = s.Timer!;
        var run      = s.Run;
        var running  = info.Status is ETimerStatus.Running or ETimerStatus.Paused;
        var finish   = running ? null : run?.Finish;
        var stopped  = !running && finish is null && run?.Stopped == true;
        var paused   = info.Status == ETimerStatus.Paused;
        var practice = _practiceModule.IsInPractice(s.Slot);

        var compare = p.Settings[HudOptions.Compare.Index];
        var tr      = p.Tr;
        var tag     = compare == HudOptions.ComparePersonalBest ? tr[HudTexts.TagPr] : tr[HudTexts.TagSr];

        float? Against(float? pb, float? wr)
            => compare switch
            {
                HudOptions.ComparePersonalBest => pb,
                HudOptions.CompareServerRecord => wr,
                _                              => null,
            };

        var view = new TimerView();

        // The time's difference: live against the record's replay, else at the last split passed, or at the finish.
        long? timeDiff = null; // ms

        if (running && compare != HudOptions.CompareOff)
        {
            var live = HudOptions.Display(HudOptions.Live, p.Settings) is { Disabled: false, Choice.Tone: HudTone.On };

            if (live && !paused)
            {
                timeDiff = TryComputePositionDelta(s, info, compare);
            }

            if (timeDiff is null && run is { Splits.Count: > 0 } && Against(run.Splits[0].CumPb, run.Splits[0].CumWr) is { } theirs)
            {
                timeDiff = HudFormat.DiffMillis(run.Splits[0].Cum, theirs);
            }
        }
        else if (finish is not null && Against(finish.Pb, finish.Wr) is { } theirs)
        {
            timeDiff = HudFormat.DiffMillis(finish.Time, theirs);
        }

        if (finish is not null)
        {
            var title = finish.Track > 0 ? tr.Format(HudTexts.BonusCompleted, finish.Track) : tr[HudTexts.MapCompleted];
            view.Title = finish.Practice ? tr.Format(HudTexts.PracticeTitle, title) : title;
        }
        else if (stopped)
        {
            (view.Title, view.TitleClass) = (tr[HudTexts.RunStopped], "stopped");
        }
        else if (paused)
        {
            (view.Title, view.TitleClass) = (tr[HudTexts.TimerPaused], "paused");
        }
        else if (practice)
        {
            (view.Title, view.TitleClass) = (tr[HudTexts.PracticeMode], "practice");
        }

        if (running || finish is not null)
        {
            view.TimeLabel = finish is not null ? tr[HudTexts.FinalTime] : tr[HudTexts.Time];
            view.Time      = HudFormat.FormatTime(finish?.Time ?? info.Time);
            view.Finished  = finish is not null;
        }

        if (timeDiff is { } diff)
        {
            view.TimeCmp     = ZString.Concat(tag, ' ', HudFormat.FormatDiff(diff));
            view.TimeCmpSign = Math.Sign(diff);
        }

        if (finish?.Stage is { } stage)
        {
            view.Stage     = stage.Stage;
            view.StageTime = HudFormat.FormatTime(stage.Time);

            if (Against(stage.Pb, stage.Wr) is { } theirs)
            {
                var stageDiff = HudFormat.DiffMillis(stage.Time, theirs);
                view.StageCmp     = ZString.Concat(tag, ' ', HudFormat.FormatDiff(stageDiff));
                view.StageCmpSign = Math.Sign(stageDiff);
            }
        }

        // The lines, by what each state shows and the player's settings.
        view.Shown[(int) HudLine.Zone]    = finish is null && p.IsOn(HudOptions.Zone);
        view.Shown[(int) HudLine.Mode]    = finish is null && p.IsOn(HudOptions.Mode);
        view.Shown[(int) HudLine.Speed]   = finish is null && p.IsOn(HudOptions.Speed);
        view.Shown[(int) HudLine.Start]   = (running || finish is not null) && p.IsOn(HudOptions.Start);
        view.Shown[(int) HudLine.Sync]    = finish is null && p.IsOn(HudOptions.Sync);
        view.Shown[(int) HudLine.Jumps]   = running && p.IsOn(HudOptions.Jumps);
        view.Shown[(int) HudLine.Strafes] = running && p.IsOn(HudOptions.Strafes);

        view.Texts[(int) HudLine.Zone]    = tr.Format(HudTexts.LineZone, ZoneName(tr, run, info, running || stopped));
        view.Texts[(int) HudLine.Mode]    = tr.Format(HudTexts.LineMode, _styleModule.GetStyleSetting(info.Style).Name);
        view.Texts[(int) HudLine.Speed]   = tr.Format(HudTexts.LineSpeed, HudFormat.RoundSpeed(speed));
        view.Texts[(int) HudLine.Sync]    = tr.Format(HudTexts.LineSync, (info.Sync * 100f).ToString("F2", CultureInfo.InvariantCulture));
        view.Texts[(int) HudLine.Jumps]   = tr.Format(HudTexts.LineJumps, info.Jumps);
        view.Texts[(int) HudLine.Strafes] = tr.Format(HudTexts.LineStrafes, info.Strafes);

        if (finish is not null)
        {
            view.Texts[(int) HudLine.Start] =
                ZString.Concat(tr.Format(HudTexts.LineEnd, finish.End), SpeedCmp(tr, tag, finish.End, Against(finish.EndPb, finish.EndWr)));
        }
        else if (running)
        {
            var start  = HudFormat.RoundSpeed(info.StartVelocity.Length2D());
            var target = compare switch
            {
                HudOptions.ComparePersonalBest => _recordModule.GetPlayerRecord(s.Slot, info.Style, info.Track),
                HudOptions.CompareServerRecord => _recordModule.GetWR(info.Style, info.Track),
                _                              => null,
            };

            var theirs = target is null ? (int?) null : HudFormat.RoundSpeed(Length2D(target.VelocityStartX, target.VelocityStartY));
            view.Texts[(int) HudLine.Start] = ZString.Concat(tr.Format(HudTexts.LineStart, start), SpeedCmp(tr, tag, start, theirs));
        }


        WriteTimer(w, p, view);
    }

    /// <summary>
    ///     Spectating a replay bot, kept simple: whose record, how far into it, and its speed. The record's own
    ///     time is in the records panel's SR line.
    /// </summary>
    private void UpdateReplayTimer(HudWriter w, HudPlayer p, IReplayBotData bot, float speed)
    {
        var view   = new TimerView();
        var header = bot.Header;

        if (bot.Status == EReplayBotStatus.Idle || header is null)
        {
            view.Title = p.Tr[HudTexts.ReplayIdle];

            if (bot.Type == EReplayBotType.Central)
            {
                view.TimeCmp = p.Tr[HudTexts.ReplayPickHint];
            }
        }
        else
        {
            view.Title      = p.Tr.Format(HudTexts.ReplayTitle, header.PlayerName, ReplayTagOf(p.Tr, bot));
            view.TitleClass = "replay";
            view.TimeLabel  = p.Tr[HudTexts.Time];

            // A central bot's pause and speed, as its controller set them.
            var elapsed = HudFormat.FormatTime(HudFormat.ReplayElapsed(bot.CurrentFrame, header.PreFrame, header.PostFrame));
            view.Time = bot.Paused ? ZString.Concat(elapsed, " ", p.Tr[HudTexts.ReplayPaused])
                : bot.Speed != 1f  ? ZString.Concat(elapsed, " [", HudFormat.SpeedTag(bot.Speed), ']')
                                     : elapsed;
        }

        view.Shown[(int) HudLine.Speed] = p.IsOn(HudOptions.Speed);
        view.Texts[(int) HudLine.Speed] = p.Tr.Format(HudTexts.LineSpeed, HudFormat.RoundSpeed(speed));

        WriteTimer(w, p, view);
    }

    private static readonly string[] HeadingGaps = ["GapTitle", "GapTime", "GapStage"];

    private static void WriteTimer(HudWriter w, HudPlayer p, TimerView view)
    {
        // Lines in the player's order; the blank line only shows between two lines that do.
        var shown = new bool[HudLines.Count];

        for (var i = 0; i < HudLines.Count; i++)
        {
            shown[i] = view.Shown[(int) p.Order[i]];
        }

        var gapAt = Array.IndexOf(p.Order, HudLine.Gap);
        shown[gapAt] = HudFormat.GapShown(shown, gapAt);

        // Heading groups, then the lines as one more; a spacer follows a group that shows when a later one does.
        var spacers = HudFormat.SpacersShown([
            view.Title is not null,
            view.Time is not null || view.TimeCmp is not null,
            view.Stage is not null,
            Array.IndexOf(shown, true) >= 0,
        ]);

        w.Class("Title", "Hidden", view.Title is null);
        w.Class("Time", "Hidden", view.Time is null);
        w.Class("TimeCmp", "Hidden", view.TimeCmp is null);
        w.Class("StageFinish", "Hidden", view.Stage is null);
        w.Class("StageCmp", "Hidden", view.StageCmp is null);

        for (var i = 0; i < HeadingGaps.Length; i++)
        {
            w.Class(HeadingGaps[i], "Hidden", !spacers[i]);
        }

        if (view.Title is not null)
        {
            w.Text("Title", "title", view.Title);
            w.Class("Title", "stopped", view.TitleClass == "stopped");
            w.Class("Title", "paused", view.TitleClass == "paused");
            w.Class("Title", "practice", view.TitleClass == "practice");
            w.Class("Title", "replay", view.TitleClass == "replay");
        }

        if (view.Time is not null)
        {
            w.Text("Time", "time", p.Tr.Format(HudTexts.TimeLine, view.TimeLabel ?? p.Tr[HudTexts.Time], view.Time));
            w.Class("Time", "finished", view.Finished);
        }

        if (view.TimeCmp is not null)
        {
            w.Text("TimeCmp", "cmp", view.TimeCmp);
            w.Class("TimeCmp", "faster", view.TimeCmpSign < 0);
            w.Class("TimeCmp", "slower", view.TimeCmpSign > 0);
        }

        if (view.Stage is { } stage)
        {
            w.Text("StageFinish", "text", p.Tr.Format(HudTexts.StageFinished, stage, view.StageTime ?? ""));
        }

        if (view.StageCmp is not null)
        {
            w.Text("StageCmp", "cmp", view.StageCmp);
            w.Class("StageCmp", "faster", view.StageCmpSign < 0);
            w.Class("StageCmp", "slower", view.StageCmpSign > 0);
        }

        // The speed line stays plain: gain / loss colours are for center speed only.
        for (var i = 0; i < HudLines.Count; i++)
        {
            var id   = HudLines.SlotIds[i];
            var line = p.Order[i];

            w.Class(id, "Hidden", !shown[i]);
            w.Class(id, "gap", line == HudLine.Gap);

            if (!shown[i] || line == HudLine.Gap)
            {
                continue; // a blank slot hides any text it still holds
            }

            w.Text(id, "text", view.Texts[(int) line] ?? "");
        }
    }

    /// <summary>
    ///     " (PR: +12 u/s)", or nothing without a speed to compare against.
    /// </summary>
    private static string SpeedCmp(HudTr tr, string tag, int mine, float? theirs)
        => theirs is { } t ? tr.Format(HudTexts.SpeedCmp, tag, HudFormat.FormatSpeedDiff(mine - t)) : "";

    private string ZoneName(HudTr tr, HudPlayer? run, ITimerInfo info, bool onTrack)
    {
        if (run is not null)
        {
            switch (run.ZoneType)
            {
                case EZoneType.Start:
                    return run.ZoneTrack > 0 ? tr.Format(HudTexts.ZoneBonusStart, run.ZoneTrack) : tr[HudTexts.ZoneMapStart];
                case EZoneType.Stage:
                    return tr.Format(HudTexts.ZoneStageStart, run.ZoneData);
                case EZoneType.End:
                    return run.ZoneTrack > 0 ? tr.Format(HudTexts.ZoneBonusEnd, run.ZoneTrack) : tr[HudTexts.ZoneMapEnd];
            }
        }

        if (!onTrack)
        {
            return tr[HudTexts.ZoneNone];
        }

        if (info.Track > 0)
        {
            return tr.Format(HudTexts.BonusN, info.Track);
        }

        if (_zoneModule.IsCurrentTrackLinear(info.Track))
        {
            return tr[HudTexts.ZoneLinear];
        }

        var stage = run is null ? null : _timerModule.GetStageTimerInfo(run.Slot) as IStageTimerInfo;

        return tr.Format(HudTexts.StageN, stage?.Stage ?? 1);
    }

    // The menu's values in the player's language; numbers and sizes read the same everywhere.
    private static string ChoiceText(HudTr tr, HudChoice choice)
        => choice.Label switch
        {
            "On"            => tr[HudTexts.ChoiceOn],
            "Off"           => tr[HudTexts.ChoiceOff],
            "Personal best" => tr[HudTexts.ChoicePersonalBest],
            "Server record" => tr[HudTexts.ChoiceServerRecord],
            "Horizontal"    => tr[HudTexts.ChoiceHorizontal],
            _               => choice.Label,
        };

    // Hide the live difference if the closest record frame is farther than this: beyond about one ramp's width the
    // projection is meaningless (the player is on a different path).
    private const float MaxPositionDiffDistSq = 256f * 256f;

    // Suppress nonsense large differences (wrong replay, teleport mid-run, ...): ten minutes, in ms.
    private const long MaxAbsPositionDelta = 600_000;

    /// <summary>
    ///     The run's time against the compared one's at the closest point of its replay: the runner's own PB, or the
    ///     server record, on the style and track they're running.
    /// </summary>
    private long? TryComputePositionDelta(HudSource s, ITimerInfo timerInfo, int compare)
    {
        if (timerInfo.Status != ETimerStatus.Running || !float.IsFinite(timerInfo.Time) || timerInfo.Time <= 0f)
        {
            return null;
        }

        ReplayContent?     replay;
        ClosestFrameIndex? frames = null;

        if (compare == HudOptions.ComparePersonalBest)
        {
            replay = _recordModule.GetPlayerRecord(s.Slot, timerInfo.Style, timerInfo.Track) is { } pb
                ? _personalBests.GetPersonalBest(s.Slot, pb, out frames)
                : null;
        }
        else
        {
            replay = _replayModule.GetCachedReplay(timerInfo.Style, timerInfo.Track, 0);
        }

        if (replay is null || replay.Frames.Count == 0)
        {
            return null;
        }

        // Spatially identical frames are common while stationary and at route crossings. Prefer the frame nearest
        // the player's elapsed-time projection, so an exact spatial tie can't jump to an unrelated point.
        var projectedFrame = replay.Header.PreFrame + ((double) timerInfo.Time / TimerConstants.TickInterval);
        var preferredFrame = (int) Math.Clamp(Math.Round(projectedFrame), 0d, replay.Frames.Count - 1d);

        var position = s.Pawn.GetAbsOrigin();
        var distSq   = float.PositiveInfinity;
        var index = compare == HudOptions.ComparePersonalBest
            ? frames?.FindClosest(position, preferredFrame, out distSq) ?? -1
            : _replayModule.FindClosestFrameIndex(timerInfo.Style, timerInfo.Track, 0, position, preferredFrame, out distSq);

        if (index < 0 || distSq > MaxPositionDiffDistSq)
        {
            return null;
        }

        // Still within the replay's lead-in: the player is at or near the start zone.
        var recordFrames = index - replay.Header.PreFrame;

        if (recordFrames <= 0)
        {
            return null;
        }

        var delta = HudFormat.DiffMillis(timerInfo.Time, recordFrames * TimerConstants.TickInterval);

        return delta is < -MaxAbsPositionDelta or > MaxAbsPositionDelta ? null : delta;
    }

    // ------------------------------------------------------------------ records, splits, keys

    /// <summary>
    ///     Server record with its holder, and the PB with its rank, for the shown run's style and track.
    /// </summary>
    private void UpdateRecords(HudWriter w, HudPlayer p, HudSource s)
    {
        var (style, track, pbSlot) = s.Replay is { } bot
            ? (bot.Style, bot.Track, p.Slot)
            : (s.Timer!.Style, s.Timer.Track, s.Slot);

        if (style < 0 || track < 0)
        {
            w.Text("Sr", "sr", p.Tr.Format(HudTexts.InfoSr, p.Tr[HudTexts.NotAvailable]));
            w.Text("Pb", "pb", p.Tr.Format(HudTexts.InfoPb, p.Tr[HudTexts.NotAvailable]));

            return;
        }

        var wr = _recordModule.GetWR(style, track);
        w.Text("Sr",
               "sr",
               p.Tr.Format(HudTexts.InfoSr,
                           wr is null ? p.Tr[HudTexts.NotAvailable] : ZString.Concat(HudFormat.FormatTime(wr.Time), " (", wr.PlayerName, ')')));

        var pb = _recordModule.GetPlayerRecord(pbSlot, style, track);
        w.Text("Pb",
               "pb",
               p.Tr.Format(HudTexts.InfoPb,
                           pb is null
                               ? p.Tr[HudTexts.NotAvailable]
                               : ZString.Concat(HudFormat.FormatTime(pb.Time), " (#", _recordModule.GetRankForTime(style, track, pb.Time), ')')));
    }

    private static readonly string[] SplitRows  = Ids("Split{0}");
    private static readonly string[] SplitNames = Ids("Split{0}Name");
    private static readonly string[] SplitTimes = Ids("Split{0}Time");
    private static readonly string[] SplitDiffs = Ids("Split{0}Diff");

    private static string[] Ids(string format)
    {
        var ids = new string[HudPlayer.MaxSplits];

        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = string.Format(CultureInfo.InvariantCulture, format, i);
        }

        return ids;
    }

    /// <summary>
    ///     Recent splits in fixed slots, newest on top. When one arrives the text moves down a slot and the list
    ///     slides in from a row higher while the top row fades in: two class changes, alternating between identical
    ///     a / b classes so the animation replays without a removal step.
    /// </summary>
    private static void UpdateSplits(HudWriter w, HudPlayer p, HudSource s)
    {
        var run     = s.Replay is null ? s.Run : null;
        var limit   = int.Parse(p.Setting(HudOptions.SplitRows), CultureInfo.InvariantCulture);
        var compare = p.Settings[HudOptions.Compare.Index];
        var fade    = p.IsOn(HudOptions.SplitFade);
        var tag     = compare == HudOptions.ComparePersonalBest ? p.Tr[HudTexts.TagPb] : p.Tr[HudTexts.TagSr];

        for (var i = 0; i < HudPlayer.MaxSplits; i++)
        {
            var split = run is not null && i < limit && i < run.Splits.Count ? run.Splits[i] : null;
            w.Class(SplitRows[i], "Hidden", split is null);

            if (i > 0)
            {
                w.Class(SplitRows[i], "nofade", !fade);
            }

            if (split is null)
            {
                continue;
            }

            var theirs = compare switch
            {
                HudOptions.ComparePersonalBest => split.Pb,
                HudOptions.CompareServerRecord => split.Wr,
                _                              => null,
            };

            w.Text(SplitNames[i], "name", p.Tr.Format(split.Stage ? HudTexts.StageN : HudTexts.CheckpointN, split.Number));
            w.Text(SplitTimes[i],
                   "time",
                   theirs is null ? HudFormat.FormatTime(split.Time) : p.Tr.Format(HudTexts.Versus, HudFormat.FormatTime(split.Time), tag));
            w.Class(SplitDiffs[i], "Hidden", theirs is null);

            if (theirs is { } t)
            {
                var diff = HudFormat.DiffMillis(split.Time, t);
                w.Text(SplitDiffs[i], "diff", HudFormat.FormatDiff(diff));
                w.Class(SplitDiffs[i], "faster", diff < 0);
                w.Class(SplitDiffs[i], "slower", diff > 0);
            }
        }

        // With the menu open, an empty panel still says what it's for, so it can be found and dragged.
        w.Class("SplitsEmpty", "shown", p.MenuOpen && (run is null || run.Splits.Count == 0));

        if (p.MenuOpen)
        {
            w.Text("SplitsEmpty", "text", p.Tr[HudTexts.SplitsEmpty]);
        }

        if (run is not null && run.SplitSerial != run.SplitShown)
        {
            run.SplitShown = run.SplitSerial;

            var flip = run.SplitSerial % 2 == 0;
            w.Class("SplitsBody", "shift-a", flip);
            w.Class("SplitsBody", "shift-b", !flip);
            w.Class("Split0", "enter-a", flip);
            w.Class("Split0", "enter-b", !flip);
        }
    }

    private static readonly (string Id, UserCommandButtons Button)[] KeyButtons =
    [
        ("KeyW", UserCommandButtons.Forward),
        ("KeyA", UserCommandButtons.MoveLeft),
        ("KeyS", UserCommandButtons.Back),
        ("KeyD", UserCommandButtons.MoveRight),
        ("KeyDuck", UserCommandButtons.Duck),
        ("KeyJump", UserCommandButtons.Jump),
    ];

    private void UpdateKeys(HudWriter w, HudPlayer p, HudSource s)
    {
        var (buttons, turn) = s.Replay is { } bot
            ? ReplayKeys(bot)
            : (s.Pawn.GetMovementService()?.KeyButtons ?? 0, _turn[s.Slot]);

        foreach (var (id, button) in KeyButtons)
        {
            w.Class(id, "on", (buttons & button) != 0);
        }

        var arrows = p.IsOn(HudOptions.KeyMouse);
        w.Class("KeyLeft", "Hidden", !arrows);
        w.Class("KeyRight", "Hidden", !arrows);
        w.Class("KeyLeft", "on", arrows && turn > 0);
        w.Class("KeyRight", "on", arrows && turn < 0);

        var jumpDuck = p.IsOn(HudOptions.KeyJumpDuck);
        w.Class("KeyDuck", "Hidden", !jumpDuck);
        w.Class("KeyGap", "Hidden", !jumpDuck);
        w.Class("KeyJump", "Hidden", !jumpDuck);

        if (jumpDuck)
        {
            w.Labels(HudLabels.Keys);
        }
    }

    private const int ReplayTurnFrames = 6; // about the 0.1 s a player's turn arrow stays lit

    /// <summary>
    ///     A replay bot's keys and turn, from the frame it last played. Playback only sets the bot's buttons for
    ///     the movement it runs each tick, so its pawn doesn't hold them between ticks, and its angles are set the
    ///     same way.
    /// </summary>
    internal static (UserCommandButtons Buttons, int Turn) ReplayKeys(IReplayBotData bot)
    {
        var frames = bot.Frames;

        if (bot.Status != EReplayBotStatus.Running || frames.Count == 0)
        {
            return (0, 0);
        }

        // Playback moves on to the next frame once it has played one.
        var current = Math.Clamp(bot.CurrentFrame - 1, 0, frames.Count - 1);
        var earlier = Math.Max(0, current - ReplayTurnFrames);
        var turned  = AngleDelta(frames[current].Angles.Y, frames[earlier].Angles.Y);

        return (frames[current].PressedButtons, MathF.Abs(turned) > 0.05f ? Math.Sign(turned) : 0);
    }
}
