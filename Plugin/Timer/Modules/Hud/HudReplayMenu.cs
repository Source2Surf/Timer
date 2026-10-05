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
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

// The replay menu (!replay, or E while spectating the central bot): pick a track, stage, style and a time on its
// leaderboard, and watch it on the central replay bot, with its controls while it's yours. It's rebuilt on every
// refresh while open, since the bot moves on and the leaderboard can change; the writer only sends what changed.
internal partial class HudModule
{
    internal const int ReplayRows = 8;

    private const int MyRunsLimit = 24; // three pages

    internal static readonly string[] ReplayRowIds  = RowIds("");
    internal static readonly string[] ReplayRankIds = RowIds("Rank");
    internal static readonly string[] ReplayNameIds = RowIds("Name");
    internal static readonly string[] ReplayTimeIds = RowIds("Time");
    internal static readonly string[] ReplayGapIds  = RowIds("Gap");

    private static readonly string[] ProgressSteps
        = Enumerable.Range(0, 51).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();

    private static string[] RowIds(string part)
        => Enumerable.Range(0, ReplayRows).Select(i => ZString.Concat("RmRow", i, part)).ToArray();

    private ECommandAction OnCommandReplay(PlayerSlot slot, StringCommand command)
    {
        if (_players[slot] is { } p)
        {
            SetReplayMenuOpen(p, !p.Replays.Open);
            RefreshNow(p);
        }

        return ECommandAction.Handled;
    }

    private void SetReplayMenuOpen(HudPlayer p, bool open)
    {
        var m = p.Replays;

        if (open && p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        if (open)
        {
            p.Profile.Open = false;
            CloseNominateMenu(p);
            CloseZonePanel(p);
            CloseRecords(p);
        }

        // Opened while playing, it starts on the player's own track and style.
        if (open && !m.Open && _timerModule.GetTimerInfo(p.Slot) is { } info
            && _bridge.TryGetController(p.Slot, out var controller) && controller.GetPlayerPawn() is { IsAlive: true })
        {
            m.Track = info.Track;
            m.Style = info.Style;
            m.Stage = 0;
            Repick(m);
        }

        if (open)
        {
            m.Runs = null; // they may have finished runs since
        }

        m.Open      = open;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void ClickReplayMenu(HudPlayer p, string buttonId)
    {
        var m = p.Replays;

        switch (buttonId)
        {
            case "RmTrackPrev" or "RmTrackNext":
                if (StepTrack(m.Track, buttonId == "RmTrackNext" ? 1 : -1) is var track and >= 0)
                {
                    m.Track = track;
                    m.Stage = 0;
                    Repick(m);
                }

                break;
            case "RmStagePrev" or "RmStageNext":
                var stage = Math.Clamp(m.Stage + (buttonId == "RmStageNext" ? 1 : -1), 0, StageCount(m.Track));

                if (stage != m.Stage)
                {
                    m.Stage = stage;
                    Repick(m);
                }

                break;
            case "RmStylePrev" or "RmStyleNext":
                var style = Math.Clamp(m.Style + (buttonId == "RmStyleNext" ? 1 : -1), 0, _styleModule.GetStyleCount() - 1);

                if (style != m.Style)
                {
                    m.Style = style;
                    Repick(m);
                }

                break;
            case "RmTabBoard" or "RmTabRuns":
                var tab = buttonId == "RmTabRuns" ? 1 : 0;

                if (tab != m.Tab)
                {
                    m.Tab = tab;
                    Repick(m);
                }

                if (tab == 1)
                {
                    m.Runs = null; // reloaded, with any run finished since
                }

                break;
            case "RmJumpWr" or "RmJumpPb":
                var records = _recordModule.GetRecords(m.Style, m.Track, m.Stage);
                var index   = buttonId == "RmJumpWr" ? (records.Count > 0 ? 0 : -1) : IndexOfPlayer(records, p.SteamId);

                if (index >= 0)
                {
                    m.Selected = index;
                    m.Page     = index / ReplayRows;
                    m.Note     = null;
                }

                break;
            case "RmPagePrev":
                m.Page = Math.Max(0, m.Page - 1);

                break;
            case "RmPageNext":
                m.Page++; // the refresh holds it to the last page

                break;
            case "RmWatch":
                ClickWatch(p);

                break;
            case "RmBack":
                _central.Seek(p.Slot, -5f);

                break;
            case "RmFwd":
                _central.Seek(p.Slot, 5f);

                break;
            case "RmPause":
                _central.TogglePause(p.Slot);

                break;
            case "RmSpeed":
                _central.CycleSpeed(p.Slot);

                break;
            case "RmStop" or "RmLeave":
                _central.Stop(p.Slot);

                break;
            case "RmClose":
                SetReplayMenuOpen(p, false);

                break;
            default:
                if (Array.IndexOf(ReplayRowIds, buttonId) is var row and >= 0)
                {
                    m.Selected = (m.Page * ReplayRows) + row;
                    m.Note     = null;
                }

                break;
        }
    }

    private void ClickWatch(HudPlayer p)
    {
        var m = p.Replays;

        if (m.Loading || _central.CentralBot is not { } bot)
        {
            return;
        }

        // Someone else's replay: watch along.
        if (_central.BusyFor(p.Slot) is not null)
        {
            _central.Spectate(p.Slot);

            return;
        }

        var list = MenuList(m);

        // Nothing picked, or it's already playing for this player.
        if ((uint) m.Selected >= (uint) list.Count || (bot.Owner == p.Slot && IsPlaying(bot, list[m.Selected])))
        {
            return;
        }

        var record       = list[m.Selected];
        var (rank, what) = Describe(p, list, m.Selected);

        m.Loading = true;
        m.Note    = null;

        _central.Watch(p.Slot, record, rank, result =>
        {
            if (_players[p.Slot] != p)
            {
                return;
            }

            m.Loading = false;

            m.Note = result switch
            {
                WatchResult.NoReplay => p.Tr.Format(HudTexts.NoReplay, what),
                WatchResult.Busy     => p.Tr[HudTexts.StartedFirst],
                WatchResult.NoBot    => p.Tr[HudTexts.NoBot],
                _                    => null,
            };

            RefreshNow(p);
        });
    }

    private void UpdateReplayMenu(HudWriter w, HudPlayer p, IPlayerController controller)
    {
        var m = p.Replays;

        // The map's tracks and the styles can differ from the last time it was open.
        if (!_zoneModule.HasZone(m.Track, EZoneType.Start))
        {
            m.Track = 0;
            m.Stage = 0;
        }

        var stages     = StageCount(m.Track);
        var styleCount = _styleModule.GetStyleCount();
        m.Stage = Math.Min(m.Stage, stages);
        m.Style = Math.Clamp(m.Style, 0, Math.Max(0, styleCount - 1));

        var pick = (m.Style, m.Track, m.Stage);

        if (m.Tab == 1 && (m.RunsFor != pick || (m.Runs is null && !m.RunsLoading)))
        {
            LoadMyRuns(p, pick);
        }

        var styleName = _styleModule.GetStyleSetting(m.Style).Name;
        var records   = MenuList(m);
        var pages     = HudFormat.PageCount(records.Count, ReplayRows);
        m.Page     = Math.Clamp(m.Page, 0, pages - 1);
        m.Selected = Math.Clamp(m.Selected, 0, Math.Max(0, records.Count - 1));

        var tr    = p.Tr;
        var board = m.Tab == 0;
        w.Labels(HudLabels.Replays);
        w.Class("RmTabBoard", "active", board);
        w.Class("RmTabBoardLabel", "active", board);
        w.Class("RmTabRuns", "active", !board);
        w.Class("RmTabRunsLabel", "active", !board);

        w.Text("RmTrackValue", "value", m.Track == 0 ? tr[HudTexts.Main] : tr.Format(HudTexts.BonusN, m.Track));
        w.Class("RmTrackPrev", "disabled", StepTrack(m.Track, -1) < 0);
        w.Class("RmTrackNext", "disabled", StepTrack(m.Track, 1) < 0);
        w.Text("RmStageValue", "value", m.Stage == 0 ? tr[HudTexts.FullRun] : tr.Format(HudTexts.StageN, m.Stage));
        w.Class("RmStagePrev", "disabled", m.Stage == 0);
        w.Class("RmStageNext", "disabled", m.Stage >= stages);
        w.Text("RmStyleValue", "value", styleName);
        w.Class("RmStylePrev", "disabled", m.Style == 0);
        w.Class("RmStyleNext", "disabled", m.Style >= styleCount - 1);
        w.Text("RmCount", "text",
               !board && m.RunsLoading ? tr[HudTexts.Loading]
               : records.Count == 1    ? tr[board ? HudTexts.TimesOne : HudTexts.RunsOne]
                                         : tr.Format(board ? HudTexts.Times : HudTexts.Runs, records.Count));

        // The shortcuts pick from the leaderboard.
        w.Class("RmJumpWr", "Hidden", !board);
        w.Class("RmJumpPb", "Hidden", !board);
        w.Class("RmJumpWr", "disabled", records.Count == 0);
        w.Class("RmJumpPb", "disabled", IndexOfPlayer(records, p.SteamId) < 0);

        var bot    = _central.CentralBot;
        var pb     = board ? null : _recordModule.GetPlayerRecord(p.Slot, m.Style, m.Track, m.Stage);
        var nowUtc = DateTime.UtcNow;

        for (var i = 0; i < ReplayRows; i++)
        {
            var index = (m.Page * ReplayRows) + i;
            var shown = index < records.Count;
            w.Class(ReplayRowIds[i], "blank", !shown);

            if (!shown)
            {
                continue;
            }

            var record = records[index];
            var you    = board && record.SteamId == p.SteamId;
            var now    = bot is not null && IsPlaying(bot, record);

            // The leaderboard compares with the SR; your own runs, when each was set and against your PB.
            var best = board ? index == 0 : pb?.Id == record.Id;
            var gap  = now  ? tr[HudTexts.Playing]
                : best      ? tr[board ? HudTexts.Sr : HudTexts.Pb]
                : board     ? HudFormat.FormatDiff(HudFormat.DiffMillis(record.Time, records[0].Time))
                : pb is not null ? HudFormat.FormatDiff(HudFormat.DiffMillis(record.Time, pb.Time))
                                   : "";

            w.Class(ReplayRowIds[i], "sel", index == m.Selected);
            w.Text(ReplayRankIds[i], "text", (index + 1).ToString(CultureInfo.InvariantCulture));
            w.Text(ReplayNameIds[i], "text", board ? (you ? tr[HudTexts.You] : record.PlayerName) : HudFormat.Ago(tr, record.RunDate, nowUtc));
            w.Class(ReplayNameIds[i], "you", you);
            w.Text(ReplayTimeIds[i], "text", HudFormat.FormatTime(record.Time));
            w.Text(ReplayGapIds[i], "text", gap);
            w.Class(ReplayGapIds[i], "now", now);
            w.Class(ReplayGapIds[i], "wr", !now && best);
        }

        w.Class("RmEmpty", "Hidden", records.Count > 0);

        if (records.Count == 0)
        {
            w.Text("RmEmpty", "text",
                   board            ? tr.Format(HudTexts.NoTimes, styleName)
                   : m.RunsLoading  ? tr[HudTexts.RunsLoading]
                                      : tr[HudTexts.NoRuns]);
        }

        w.Text("RmPage", "text", tr.Format(HudTexts.Page, m.Page + 1, pages));
        w.Class("RmPagePrev", "disabled", m.Page == 0);
        w.Class("RmPageNext", "disabled", m.Page >= pages - 1);

        // Watching the central bot: what's playing, and its controls for whoever started it.
        var header   = bot?.Header;
        var watching = bot is { Status: not EReplayBotStatus.Idle } && header is not null && ObservedSlot(controller) == bot.Slot;
        var mine     = watching && _central.CanControl(p.Slot); // they started it, or may control it anyway
        var busy     = _central.BusyFor(p.Slot);

        w.Class("RmNow", "Hidden", !watching);
        w.Class("RmStatus", "Hidden", watching);

        if (watching)
        {
            var elapsed = HudFormat.ReplayElapsed(bot!.CurrentFrame, header!.PreFrame, header.PostFrame);

            w.Text("RmNowName", "text", header.PlayerName);
            w.Text("RmNowTag", "text", ReplayTagOf(tr, bot));
            w.Text("RmNowTime", "text", ZString.Concat(HudFormat.FormatTime(elapsed), " / ", HudFormat.FormatTime(bot.Time)));
            w.Numbered("RmBarFill", "w", ProgressSteps[HudFormat.ProgressStep(elapsed, bot.Time)]);
            w.Class("RmCtl", "Hidden", !mine);
            w.Class("RmAlong", "Hidden", mine);

            if (mine)
            {
                var fast = bot.Speed != 1f;
                w.Text("RmPauseLabel", "text", tr[bot.Paused ? HudTexts.Play : HudTexts.Pause]);
                w.Class("RmPause", "lit", bot.Paused);
                w.Class("RmPauseLabel", "lit", bot.Paused);
                w.Text("RmSpeedLabel", "text", HudFormat.SpeedTag(bot.Speed));
                w.Class("RmSpeed", "lit", fast);
                w.Class("RmSpeedLabel", "lit", fast);
            }
            else
            {
                w.Text("RmAlongText", "text",
                       bot.Owner is { } owner && _bridge.ClientManager.GetGameClient(owner) is { } client
                           ? tr.Format(HudTexts.AlongOwner, client.Name)
                           : tr[HudTexts.AlongLeft]);
            }
        }
        else
        {
            string status;

            if (bot is null)
            {
                status = tr[HudTexts.NoBot];
            }
            else if (m.Loading && (uint) m.Selected < (uint) records.Count)
            {
                status = tr.Format(HudTexts.LoadingWhat, Describe(p, records, m.Selected).What);
            }
            else if (m.Note is { } note)
            {
                status = note;
            }
            else if (busy is { } owner && bot.Header is { } busyHeader)
            {
                status = tr.Format(HudTexts.Busy,
                                   _bridge.ClientManager.GetGameClient(owner)?.Name ?? tr[HudTexts.Someone],
                                   HudFormat.Whose(tr, busyHeader.PlayerName, bot.Rank, false));
            }
            else if (bot.Status == EReplayBotStatus.Idle && ObservedSlot(controller) == bot.Slot)
            {
                // Spectating it after a replay ended (or before one): no Stop button, so say how to get back.
                status = tr[HudTexts.BotFree];
            }
            else
            {
                status = tr[HudTexts.MenuHint];
            }

            w.Text("RmStatus", "text", status);
            w.Class("RmStatus", "warn", m.Note is not null && !m.Loading);
        }

        var selected = (uint) m.Selected < (uint) records.Count ? records[m.Selected] : null;

        var (label, enabled) = bot switch
        {
            null                                               => (tr[HudTexts.NoBotButton], false),
            _ when busy is not null                            => watching ? (tr[HudTexts.WatchingAlong], false) : (tr[HudTexts.WatchAlong], true),
            _ when m.Loading                                   => (tr[HudTexts.Loading], false),
            _ when selected is null                            => (tr[HudTexts.Watch], false),
            _ when mine && IsPlaying(bot, selected)            => (tr[HudTexts.PlayingButton], false),
            _ => (tr.Format(mine ? HudTexts.PlayWhat : HudTexts.WatchWhat, Describe(p, records, m.Selected).What), true),
        };

        w.Text("RmWatchLabel", "text", label);
        w.Class("RmWatch", "disabled", !enabled);
        w.Class("RmWatchLabel", "disabled", !enabled);
    }

    // The list the menu shows: the leaderboard, or the player's own recent runs.
    private IReadOnlyList<RunRecord> MenuList(HudReplayMenu m)
        => m.Tab == 0 ? _recordModule.GetRecords(m.Style, m.Track, m.Stage) : m.Runs ?? [];

    /// <summary>
    ///     How a row of the list plays: its leaderboard rank (0 for an own run that isn't the player's best, which
    ///     isn't on it), and what the menu calls it.
    /// </summary>
    private (int Rank, string What) Describe(HudPlayer p, IReadOnlyList<RunRecord> list, int index)
    {
        var m      = p.Replays;
        var record = list[index];

        if (m.Tab == 0)
        {
            return (index + 1, HudFormat.Whose(p.Tr, record.PlayerName, index + 1, record.SteamId == p.SteamId));
        }

        var leaderboard = _recordModule.GetRecords(m.Style, m.Track, m.Stage);
        var at          = IndexOfPlayer(leaderboard, p.SteamId);

        return (at >= 0 && leaderboard[at].Id == record.Id ? at + 1 : 0, HudFormat.OwnRun(p.Tr, record.Time));
    }

    private static bool IsPlaying(IReplayBotData bot, RunRecord record)
        => bot.Status != EReplayBotStatus.Idle && bot.RunId == record.Id;

    // The player's recent finishes on the picked leaderboard, slower ones included, newest first.
    private void LoadMyRuns(HudPlayer p, (int Style, int Track, int Stage) pick)
    {
        var m = p.Replays;
        m.RunsFor     = pick;
        m.RunsLoading = true;
        m.Runs        = null;

        var mapName = _bridge.CurrentMapName;
        var steamId = new SteamID(p.SteamId);

        _ = Task.Run(async () =>
        {
            IReadOnlyList<RunRecord> runs;

            try
            {
                runs = await _request.GetPlayerRuns(mapName, steamId, pick.Style, pick.Track, pick.Stage, MyRunsLimit)
                                     .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to load the runs of {SteamId}", p.SteamId);
                runs = [];
            }

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_players[p.Slot] == p && m.RunsFor == pick)
                {
                    m.Runs        = runs;
                    m.RunsLoading = false;
                }
            }).ConfigureAwait(false);
        });
    }

    private string ReplayTagOf(HudTr tr, IReplayBotData bot)
        => HudFormat.ReplayTag(tr, bot.Style > 0 ? _styleModule.GetStyleSetting(bot.Style).Name : null, bot.Track, bot.Stage, bot.Rank);

    /// <summary>
    ///     The next track with a start zone from <paramref name="track" /> in direction <paramref name="d" />, or -1.
    /// </summary>
    private int StepTrack(int track, int d)
    {
        for (var t = track + d; t >= 0 && t < TimerConstants.MAX_TRACK; t += d)
        {
            if (_zoneModule.HasZone(t, EZoneType.Start))
            {
                return t;
            }
        }

        return -1;
    }

    // A linear track has no stages to pick.
    private int StageCount(int track)
        => _zoneModule.IsCurrentTrackLinear(track) ? 0 : _zoneModule.GetTotalStages(track);

    /// <summary>
    ///     Where the player is on a leaderboard, or -1.
    /// </summary>
    private static int IndexOfPlayer(IReadOnlyList<RunRecord> records, ulong steamId)
    {
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].SteamId == steamId)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Repick(HudReplayMenu m)
    {
        m.Page     = 0;
        m.Selected = 0;
        m.Note     = null;
    }

    private PlayerSlot? ObservedSlot(IPlayerController controller)
        => controller.GetPawn()?.AsObserver() is { } observer ? ObservedSlot(observer) : null;

    private PlayerSlot? ObservedSlot(IObserverPawn observer)
        => observer.GetObserverService() is { ObserverMode: not (ObserverMode.None or ObserverMode.Roaming) } service
           && service.ObserverTarget.IsValid()
           && _bridge.EntityManager.FindEntityByHandle(service.ObserverTarget)?.AsPlayerPawn()?.GetController() is { } target
            ? target.PlayerSlot
            : null;
}
