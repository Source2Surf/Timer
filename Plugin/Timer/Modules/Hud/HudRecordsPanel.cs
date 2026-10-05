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
using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

// The records panel (!wr / !sr): a board per style, track and stage, one row per player, for this map or another.
// Admins with timer:records can delete the picked run and its replays.
internal partial class HudModule
{
    internal const int RecordRows = 8;

    internal static readonly string[] RecordRowIds  = RecordIds("");
    internal static readonly string[] RecordRankIds = RecordIds("Rank");
    internal static readonly string[] RecordNameIds = RecordIds("Name");
    internal static readonly string[] RecordTimeIds = RecordIds("Time");
    internal static readonly string[] RecordGapIds  = RecordIds("Gap");

    private static string[] RecordIds(string part)
        => Enumerable.Range(0, RecordRows).Select(i => ZString.Concat("LbRow", i, part)).ToArray();

    private void OnLeaderboardRequested(PlayerSlot slot, string? map)
    {
        if (_players[slot] is not { } p)
        {
            return;
        }

        // !wr again for the same map closes it.
        if (p.Records.Open && string.Equals(p.Records.Map, map, StringComparison.OrdinalIgnoreCase))
        {
            CloseRecords(p);
        }
        else
        {
            OpenRecords(p, map);
        }
    }

    private void OpenRecords(HudPlayer p, string? map)
    {
        var r = p.Records;

        if (p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        p.Replays.Open = false;
        p.Profile.Open = false;
        CloseNominateMenu(p);
        CloseZonePanel(p);

        // The player's own style, and on this map their track.
        var info = _timerModule.GetTimerInfo(p.Slot);
        r.Map       = map;
        r.Style     = info?.Style ?? 0;
        r.Track     = map is null ? info?.Track ?? 0 : 0;
        r.Stage     = 0;
        r.Page      = 0;
        r.Picked    = null;
        r.PickFirst = true;
        r.Confirm   = null;
        r.Deleting  = null;
        r.ShowDeleted = false;
        r.Note      = null;
        r.Open      = true;
        r.Dirty     = true;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private static void CloseRecords(HudPlayer p)
    {
        var r = p.Records;

        if (!r.Open)
        {
            return;
        }

        r.Open      = false;
        r.Confirm   = null;
        r.Note      = null;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void UpdateLeaderboard(HudWriter w, HudPlayer p)
    {
        var r = p.Records;
        w.Class("LbMenu", "Closed", !r.Open);

        if (!r.Open)
        {
            return;
        }

        // Boards load, reload and lose runs (an admin delete) on the game thread.
        var version = _recordModule.RecordsVersion;

        if (version != r.Version)
        {
            r.Version = version;
            r.Dirty   = true;
        }

        if (r.Dirty)
        {
            r.Dirty = false;
            DrawLeaderboard(w, p);
        }
    }

    private string RecordsMap(HudRecords r)
        => r.Map ?? _bridge.CurrentMapName;

    private void DrawLeaderboard(HudWriter w, HudPlayer p)
    {
        var r          = p.Records;
        var tr         = p.Tr;
        var map        = RecordsMap(r);
        var styleCount = _styleModule.GetStyleCount();
        var boards     = r.Map is null ? null : _recordModule.GetBoards(r.Map);

        // This map's tracks can change between maps; another map's come from its boards.
        if (r.Map is null && !_zoneModule.HasZone(r.Track, EZoneType.Start))
        {
            r.Track = 0;
            r.Stage = 0;
        }

        r.Style = Math.Clamp(r.Style, 0, Math.Max(0, styleCount - 1));

        var records   = _recordModule.GetRecords(map, r.Style, r.Track, r.Stage); // null while another map loads
        var list      = records ?? (IReadOnlyList<RunRecord>) [];
        var loading   = records is null;
        var styleName = _styleModule.GetStyleSetting(r.Style).Name;

        // The picked run, found again by id: after a delete it's gone, or the player's row is a different run.
        var picked = r.Picked is { } id ? IndexOfRecord(list, id) : -1;

        if (picked < 0 && r.Picked is not null && !loading)
        {
            if (r.Deleting == r.Picked)
            {
                r.ShowDeleted = true;
                r.Deleting    = null;
            }

            r.Picked  = null;
            r.Confirm = null;
        }

        // What took the deleted run's place, once the board has it (another map's refetch lands a little later).
        if (r.ShowDeleted && !loading)
        {
            var next = IndexOfPlayer(list, r.DeletedSteamId);
            r.Note = next >= 0
                ? tr.Format(HudTexts.LbDeletedNext, r.DeletedName!, r.DeletedTime!, HudFormat.FormatTime(list[next].Time))
                : tr.Format(HudTexts.LbDeleted, r.DeletedName!, r.DeletedTime!);
            r.Warn = false;
        }

        if (r.PickFirst && !loading)
        {
            r.PickFirst = false;
            r.Page      = 0;
            picked      = list.Count > 0 ? 0 : -1;
            r.Picked    = picked >= 0 ? list[0].Id : null;
        }

        var pages = HudFormat.PageCount(list.Count, RecordRows);
        r.Page = Math.Clamp(r.Page, 0, pages - 1);

        w.Labels(HudLabels.Records);
        w.Text("LbMap", "text", map);
        w.Text("LbTrackValue", "value", r.Track == 0 ? tr[HudTexts.Main] : tr.Format(HudTexts.BonusN, r.Track));
        w.Class("LbTrackPrev", "disabled", StepRecordsTrack(r, boards, -1) < 0);
        w.Class("LbTrackNext", "disabled", StepRecordsTrack(r, boards, 1) < 0);
        w.Text("LbStageValue", "value", r.Stage == 0 ? tr[HudTexts.FullRun] : tr.Format(HudTexts.StageN, r.Stage));
        w.Class("LbStagePrev", "disabled", StepRecordsStage(r, boards, -1) < 0);
        w.Class("LbStageNext", "disabled", StepRecordsStage(r, boards, 1) < 0);
        w.Text("LbStyleValue", "value", styleName);
        w.Class("LbStylePrev", "disabled", r.Style == 0);
        w.Class("LbStyleNext", "disabled", r.Style >= styleCount - 1);
        w.Text("LbCount", "text", loading ? tr[HudTexts.Loading] : list.Count == 1 ? tr[HudTexts.TimesOne] : tr.Format(HudTexts.Times, list.Count));
        w.Class("LbJumpSr", "disabled", list.Count == 0);
        w.Class("LbJumpYou", "disabled", IndexOfPlayer(list, p.SteamId) < 0);

        for (var i = 0; i < RecordRows; i++)
        {
            var index = (r.Page * RecordRows) + i;
            var shown = index < list.Count;
            w.Class(RecordRowIds[i], "blank", !shown);
            r.RowIds[i] = shown ? list[index].Id : 0;

            if (!shown)
            {
                continue;
            }

            var record = list[index];
            var you    = record.SteamId == p.SteamId;
            w.Class(RecordRowIds[i], "sel", index == picked);
            w.Text(RecordRankIds[i], "text", (index + 1).ToString(CultureInfo.InvariantCulture));
            w.Text(RecordNameIds[i], "text", you ? tr[HudTexts.You] : record.PlayerName);
            w.Class(RecordNameIds[i], "you", you);
            w.Text(RecordTimeIds[i], "text", HudFormat.FormatTime(record.Time));
            w.Text(RecordGapIds[i], "text", index == 0 ? tr[HudTexts.Sr] : HudFormat.FormatDiff(HudFormat.DiffMillis(record.Time, list[0].Time)));
            w.Class(RecordGapIds[i], "wr", index == 0);
        }

        w.Class("LbEmpty", "Hidden", list.Count > 0);

        if (list.Count == 0)
        {
            w.Text("LbEmpty", "text", loading ? tr.Format(HudTexts.LbLoading, map) : tr.Format(HudTexts.NoTimes, styleName));
        }

        w.Text("LbPage", "text", tr.Format(HudTexts.Page, r.Page + 1, pages));
        w.Class("LbPrev", "disabled", r.Page == 0);
        w.Class("LbNext", "disabled", r.Page >= pages - 1);

        // The picked run: who, when, and how. Kept in place when there's none, so the panel doesn't jump.
        w.Class("LbCard", "blank", picked < 0);

        if (picked >= 0)
        {
            var record = list[picked];
            w.Text("LbCardName", "text", record.SteamId == p.SteamId ? tr[HudTexts.You] : record.PlayerName);
            w.Text("LbCardTime", "text", HudFormat.FormatTime(record.Time));
            w.Text("LbCardRank", "text",
                   ZString.Concat(picked == 0 ? tr[HudTexts.Sr] : tr.Format(HudTexts.RankOf, picked + 1, list.Count),
                                  " · ",
                                  HudFormat.Ago(tr, record.RunDate, DateTime.UtcNow)));
            w.Text("LbCardStats", "text", tr.Format(HudTexts.LbStats, record.Jumps, record.Strafes, HudFormat.Percent(record.Sync)));
            w.Text("LbCardSpeed", "text",
                   tr.Format(HudTexts.LbSpeed,
                             Speed2D(record.VelocityStartX, record.VelocityStartY),
                             Speed2D(record.VelocityAvgX, record.VelocityAvgY),
                             Speed2D(record.VelocityEndX, record.VelocityEndY)));
        }

        // Deleting, for admins only.
        var admin = _recordModule.CanDeleteRecords(p.Slot);
        w.Class("LbAdminNote", "Hidden", !admin);
        w.Class("LbNote", "Hidden", !admin);
        w.Class("LbDelete", "Hidden", !admin);

        if (admin)
        {
            var confirm = picked >= 0 && r.Confirm == r.Picked;
            var off     = picked < 0 || r.Deleting == r.Picked;
            w.Text("LbDeleteLabel", "text", tr[confirm ? HudTexts.LbConfirm : HudTexts.LbDelete]);
            w.Class("LbDelete", "confirm", confirm);
            w.Class("LbDelete", "disabled", off);
            w.Class("LbDeleteLabel", "disabled", off);
            w.Text("LbNote", "text", r.Note ?? "");
            w.Class("LbNote", "warn", r.Note is not null && r.Warn);
        }
    }

    private static string Speed2D(float x, float y)
        => HudFormat.Count((long) MathF.Round(MathF.Sqrt((x * x) + (y * y))));

    private static int IndexOfRecord(IReadOnlyList<RunRecord> records, long id)
    {
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    // This map steps through its zones' tracks and stages; another map through the boards it has.
    private int StepRecordsTrack(HudRecords r, IReadOnlyList<(int Style, int Track, int Stage)>? boards, int d)
    {
        if (r.Map is null)
        {
            return StepTrack(r.Track, d);
        }

        var best = -1;

        if (boards is null)
        {
            return best;
        }

        foreach (var (_, track, _) in boards)
        {
            if ((d > 0 ? track > r.Track : track < r.Track) && (best < 0 || (d > 0 ? track < best : track > best)))
            {
                best = track;
            }
        }

        // The main track is always there to go back to.
        return best < 0 && d < 0 && r.Track > 0 ? 0 : best;
    }

    private int StepRecordsStage(HudRecords r, IReadOnlyList<(int Style, int Track, int Stage)>? boards, int d)
    {
        if (r.Map is null)
        {
            var stage = r.Stage + d;

            return stage >= 0 && stage <= StageCount(r.Track) ? stage : -1;
        }

        var best = -1;

        if (boards is null)
        {
            return best;
        }

        foreach (var (_, track, stage) in boards)
        {
            if (track == r.Track && (d > 0 ? stage > r.Stage : stage < r.Stage) && (best < 0 || (d > 0 ? stage < best : stage > best)))
            {
                best = stage;
            }
        }

        return best < 0 && d < 0 && r.Stage > 0 ? 0 : best;
    }

    // ------------------------------------------------------------------ clicks

    private void ClickRecords(HudPlayer p, string buttonId)
    {
        var r = p.Records;

        if (!r.Open)
        {
            return;
        }

        var boards = r.Map is null ? null : _recordModule.GetBoards(r.Map);
        var keep   = false;
        var board  = false; // a different board: pick its first run
        r.ShowDeleted = false;

        switch (buttonId)
        {
            case "LbClose":
                CloseRecords(p);

                return;
            case "LbTrackPrev" or "LbTrackNext" when StepRecordsTrack(r, boards, buttonId == "LbTrackNext" ? 1 : -1) is var track and >= 0:
                r.Track = track;
                r.Stage = 0;
                board   = true;

                break;
            case "LbStagePrev" or "LbStageNext" when StepRecordsStage(r, boards, buttonId == "LbStageNext" ? 1 : -1) is var stage and >= 0:
                r.Stage = stage;
                board   = true;

                break;
            case "LbStylePrev" or "LbStyleNext":
                r.Style = Math.Clamp(r.Style + (buttonId == "LbStyleNext" ? 1 : -1), 0, Math.Max(0, _styleModule.GetStyleCount() - 1));
                board   = true;

                break;
            case "LbPrev" or "LbNext":
                r.Page += buttonId == "LbNext" ? 1 : -1;

                break;
            case "LbJumpSr" or "LbJumpYou":
            {
                var list  = _recordModule.GetRecords(RecordsMap(r), r.Style, r.Track, r.Stage) ?? [];
                var index = buttonId == "LbJumpSr" ? (list.Count > 0 ? 0 : -1) : IndexOfPlayer(list, p.SteamId);

                if (index >= 0)
                {
                    r.Picked = list[index].Id;
                    r.Page   = index / RecordRows;
                }

                break;
            }
            case "LbDelete":
                keep = DeleteRecord(p);

                break;
            default:
                if (Array.IndexOf(RecordRowIds, buttonId) is var row and >= 0 && r.RowIds[row] is var id and not 0)
                {
                    r.Picked = id;
                }

                break;
        }

        if (board)
        {
            r.Page      = 0;
            r.Picked    = null;
            r.PickFirst = true;
            r.Note      = null;
        }

        if (!keep)
        {
            r.Confirm = null;
        }

        r.Dirty = true;
    }

    // The first click asks, the second deletes. Returns whether it's waiting for the second.
    private bool DeleteRecord(HudPlayer p)
    {
        var r = p.Records;

        if (!_recordModule.CanDeleteRecords(p.Slot) || r.Picked is not { } id || r.Deleting == id
            || _recordModule.GetRecords(RecordsMap(r), r.Style, r.Track, r.Stage) is not { } list)
        {
            return false;
        }

        var index = IndexOfRecord(list, id);

        if (index < 0)
        {
            return false;
        }

        var record = list[index];
        var time   = HudFormat.FormatTime(record.Time);

        if (r.Confirm != id)
        {
            r.Confirm = id;
            r.Note    = p.Tr.Format(HudTexts.LbConfirmAsk, record.PlayerName, time);
            r.Warn    = true;

            return true;
        }

        // The admin's chat line says how it went; the board redraws when it reloads.
        _recordModule.DeleteRecord(p.Slot, record, r.Map);
        r.Deleting       = id;
        r.DeletedSteamId = record.SteamId;
        r.DeletedName    = record.PlayerName;
        r.DeletedTime    = time;
        r.Note           = p.Tr.Format(HudTexts.LbDeleting, record.PlayerName, time);
        r.Warn           = false;

        return false;
    }
}
