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
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;
using Source2Surf.Timer.Utilities;

namespace Source2Surf.Timer.Modules;

// The profile card (!profile, !profile name, !profile SteamID): a player's overall results for one style, then this
// map's for that style and a track; a SteamID also finds players who aren't on the server. It's rebuilt on every
// refresh while open (PBs can change); the writer only sends changes.
internal partial class HudModule
{
    internal const int ProfileStageTiles = 12;

    internal static readonly string[] ProfileStageIds     = StageIds("");
    internal static readonly string[] ProfileStageNameIds = StageIds("Name");
    internal static readonly string[] ProfileStageTimeIds = StageIds("Time");

    private static string[] StageIds(string part)
        => Enumerable.Range(0, ProfileStageTiles).Select(i => ZString.Concat("PfStage", i, part)).ToArray();

    private ECommandAction OnCommandProfile(PlayerSlot slot, StringCommand command)
    {
        if (_players[slot] is not { } p)
        {
            return ECommandAction.Handled;
        }

        var name = command.ArgString.Trim();

        if (string.IsNullOrEmpty(name))
        {
            if (p.Profile is { Open: true, Offline: false } && p.Profile.Target == slot)
            {
                SetProfileOpen(p, false);
            }
            else
            {
                OpenProfile(p, slot);
            }
        }
        else if (SteamIds.TryParse(name, out var steamId))
        {
            if (_bridge.ClientManager.GetGameClient(new SteamID(steamId)) is { IsFakeClient: false } online)
            {
                OpenProfile(p, online.Slot);
            }
            else
            {
                OpenOfflineProfile(p, steamId);
            }
        }
        else if (FindPlayer(p, name, out var target) is { } problem)
        {
            if (_bridge.TryGetController(slot, out var controller))
            {
                controller.PrintToChat(problem);
            }
        }
        else
        {
            OpenProfile(p, target);
        }

        RefreshNow(p);

        return ECommandAction.Handled;
    }

    /// <summary>
    ///     The player on the server called <paramref name="name" />: an exact match (ignoring case), else the only
    ///     one whose name contains it. Returns why there's none, or null.
    /// </summary>
    private string? FindPlayer(HudPlayer p, string name, out PlayerSlot found)
    {
        found = default;
        var matches = 0;

        foreach (var other in _players)
        {
            if (other is null || _bridge.ClientManager.GetGameClient(other.Slot) is not { } client)
            {
                continue;
            }

            if (client.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = other.Slot;

                return null;
            }

            if (client.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                found = other.Slot;
                matches++;
            }
        }

        return matches switch
        {
            1 => null,
            0 => p.Tr.Format(HudTexts.FindNone, name),
            _ => p.Tr.Format(HudTexts.FindMany, name),
        };
    }

    private void OpenProfile(HudPlayer p, PlayerSlot target)
    {
        if (_bridge.ClientManager.GetGameClient(target) is not { } client)
        {
            return;
        }

        // It starts on what they're playing.
        var info = _timerModule.GetTimerInfo(target);
        ShowProfile(p, (ulong) client.SteamId, target, false, info?.Style ?? 0, info?.Track ?? 0);
    }

    // Someone who isn't on the server: what the backend has. A SteamID that has never played here closes it again.
    private void OpenOfflineProfile(HudPlayer p, ulong steamId)
        => ShowProfile(p, steamId, default, true, _styleModule.DefaultStyle, 0);

    private void ShowProfile(HudPlayer p, ulong steamId, PlayerSlot target, bool offline, int style, int track)
    {
        if (p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        p.Replays.Open = false;
        CloseNominateMenu(p);
        CloseZonePanel(p);
        CloseRecords(p);
        CloseStyles(p);
        CloseMapInfo(p);

        var f = p.Profile;
        f.Open            = true;
        f.Version++;
        f.Target          = target;
        f.Offline         = offline;
        f.SteamId         = steamId;
        f.Records         = null;
        f.Rank            = 0;
        f.RankOf          = 0;
        f.Summary         = null;
        f.SummaryFetched  = false;
        f.MapPlayTime     = 0;
        f.MapPlays        = 0;
        f.MapStatsFetched = false;

        f.Style = style;
        f.Track = track;

        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);

        var version = f.Version;
        var id      = new SteamID(steamId);
        var mapName = _bridge.CurrentMapName;

        FetchForProfile(p, version, "GetPlayerPointsRank", () => _request.GetPlayerPointsRank(id),
                        (profile, rank) => (profile.Rank, profile.RankOf) = rank);
        FetchForProfile(p, version, "GetPlayerSummary", () => _request.GetPlayerSummary(id),
                        (profile, summary) =>
                        {
                            (profile.Summary, profile.SummaryFetched) = (summary, true);

                            if (profile.Offline && summary is { Name: null })
                            {
                                SetProfileOpen(p, false);

                                if (_bridge.TryGetController(p.Slot, out var controller))
                                {
                                    controller.PrintToChat(p.Tr.Format(HudTexts.FindUnknown, steamId));
                                }
                            }
                        },
                        profile => profile.SummaryFetched = true);
        FetchForProfile(p, version, "GetPlayerMapStatsAsync", () => _request.GetPlayerMapStatsAsync(id, mapName),
                        (profile, stats) => (profile.MapPlayTime, profile.MapPlays, profile.MapStatsFetched) = (stats.playTime, stats.playCount, true),
                        profile => profile.MapStatsFetched = true);

        if (offline)
        {
            FetchForProfile(p, version, "GetPlayerRecords", async () =>
                            {
                                var main   = await _request.GetPlayerRecords(id, mapName).ConfigureAwait(false);
                                var stages = await _request.GetPlayerStageRecords(id, mapName).ConfigureAwait(false);

                                return (IReadOnlyList<RunRecord>) [..main, ..stages];
                            },
                            (profile, records) => profile.Records = records,
                            profile => profile.Records = []);
        }
    }

    // An offline player's PB here, from the fetched records.
    private static RunRecord? OfflineRecord(HudProfile f, int track, int stage)
        => f.Records?.Where(r => r.Style == f.Style && r.Track == track && r.Stage == stage).MinBy(r => r.Time);

    // Runs a query for the open profile and applies its answer on the game thread, unless the card has moved on
    // to another profile (or closed and reopened) since.
    private void FetchForProfile<T>(HudPlayer                 p,
                                    int                       version,
                                    string                    what,
                                    Func<Task<T>>             query,
                                    Action<HudProfile, T>     apply,
                                    Action<HudProfile>?       failed = null)
    {
        _ = Task.Run(async () =>
        {
            T    answer = default!;
            var  ok     = true;

            try
            {
                answer = await query().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                ok = false;
                _logger.LogWarning(e, "{Query} failed for a profile", what);
            }

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_players[p.Slot] != p || p.Profile.Version != version)
                {
                    return;
                }

                if (ok)
                {
                    apply(p.Profile, answer);
                }
                else
                {
                    failed?.Invoke(p.Profile);
                }
            }).ConfigureAwait(false);
        });
    }

    private void SetProfileOpen(HudPlayer p, bool open)
    {
        if (open)
        {
            if (p.Profile.Offline)
            {
                OpenOfflineProfile(p, p.Profile.SteamId);
            }
            else
            {
                OpenProfile(p, p.Profile.Target);
            }

            return;
        }

        p.Profile.Open = false;
        p.Profile.Version++; // drops answers still on their way
        p.MenuDirty    = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void ClickProfile(HudPlayer p, string buttonId)
    {
        var f = p.Profile;

        switch (buttonId)
        {
            case "PfStylePrev" or "PfStyleNext":
                f.Style = _styleModule.StepStyle(f.Style, buttonId == "PfStyleNext" ? 1 : -1);

                break;
            case "PfTrackPrev" or "PfTrackNext":
                if (StepTrack(f.Track, buttonId == "PfTrackNext" ? 1 : -1) is var track and >= 0)
                {
                    f.Track = track;
                }

                break;
            case "PfClose":
                SetProfileOpen(p, false);

                break;
        }
    }

    private static readonly string[] Lifts = ["1", "2"];

    // A value and the small note after it. Panorama can't align their baselines, so a note in a CJK script is lifted
    // onto the value's (HudFormat.NoteLift; hud.css .PfSmall.lift-N).
    private static void ProfileValue(HudWriter w, string id, string noteId, string value, string note)
    {
        w.Text(id, "text", value);
        w.Text(noteId, "text", note);

        var lift = HudFormat.NoteLift(value, note);
        w.Numbered(noteId, "lift", lift > 0 ? Lifts[lift - 1] : null);
    }

    private void UpdateProfile(HudWriter w, HudPlayer p)
    {
        var f = p.Profile;

        var client = f.Offline ? null : _bridge.ClientManager.GetGameClient(f.Target);

        // They left: nothing more to show.
        if (!f.Offline && (client is null || (ulong) client.SteamId != f.SteamId))
        {
            SetProfileOpen(p, false);
            w.Class("PfMenu", "Closed", true);

            return;
        }

        if (!_zoneModule.HasZone(f.Track, EZoneType.Start))
        {
            f.Track = 0;
        }

        f.Style = _styleModule.ValidStyle(f.Style);

        var styleName = _styleModule.GetStyleSetting(f.Style).Name;
        // Someone online is what the server has loaded; someone offline what the backend's summary says.
        var profile = f.Offline ? null : _playerManager.GetPlayerProfile(f.Target);
        var session = f.Offline ? 0 : _recordModule.GetSessionTime(f.Target); // this visit, which the stored play time doesn't have yet
        var points  = f.Offline ? f.Summary?.Points : profile?.Points;
        var joined  = f.Offline
            ? f.Summary is { JoinedAt: > 0 } s ? DateTimeOffset.FromUnixTimeMilliseconds(s.JoinedAt).UtcDateTime : (DateTime?) null
            : profile?.JoinDate;

        var tr = p.Tr;
        w.Labels(HudLabels.Profile);

        // Header
        w.Text("PfName", "text", client?.Name ?? f.Summary?.Name ?? "…");
        w.Text("PfRank", "text",
               points is not { } pts ? ""
               : f.RankOf > 0        ? tr.Format(HudTexts.ProfileRank, HudFormat.Count(f.Rank), HudFormat.Count(f.RankOf), HudFormat.Count(pts))
                                       : tr.Format(HudTexts.ProfilePoints, HudFormat.Count(pts)));
        w.Text("PfJoined", "text",
               ZString.Concat(joined is not { } date ? "" : tr.Format(HudTexts.Joined, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                              f.Summary is { } played ? tr.Format(HudTexts.Played, HudFormat.Duration(tr, played.PlayTime + session)) : "",
                              !f.Offline && _countries.GetShownCountry(f.Target) is { } country ? tr.Format(HudTexts.From, country.Name) : ""));

        w.Text("PfStyleValue", "value", styleName);
        w.Class("PfStylePrev", "disabled", _styleModule.StepStyle(f.Style, -1) == f.Style);
        w.Class("PfStyleNext", "disabled", _styleModule.StepStyle(f.Style, 1) == f.Style);

        // Overall, for the picked style; left out when the request provider can't tell.
        var overall = !f.SummaryFetched || f.Summary is not null;
        w.Class("PfOverall", "Hidden", !overall);

        if (overall)
        {
            w.Text("PfOverallTitle", "text", tr.Format(HudTexts.Overall, styleName));

            if (f.Summary is { } summary)
            {
                var style = summary.Styles.FirstOrDefault(x => x.Style == f.Style);
                var maps     = style?.MapsCompleted ?? 0;
                var bonuses  = style?.BonusesCompleted ?? 0;

                ProfileValue(w, "PfMaps", "PfMapsOf", HudFormat.Count(maps), HudFormat.OfTotal(tr, maps, summary.TotalMaps));
                ProfileValue(w, "PfBonuses", "PfBonusesOf", HudFormat.Count(bonuses), HudFormat.OfTotal(tr, bonuses, summary.TotalBonuses));
                w.Text("PfRecords", "text", tr.Format(HudTexts.RecordsHeld, style?.MapRecords ?? 0, style?.BonusRecords ?? 0, style?.StageRecords ?? 0));
            }
            else
            {
                ProfileValue(w, "PfMaps", "PfMapsOf", "…", "");
                ProfileValue(w, "PfBonuses", "PfBonusesOf", "…", "");
                w.Text("PfRecords", "text", "…");
            }
        }

        // This map: the time here is the whole map's (this visit counts as a play); the PB and stages follow the
        // style and track.
        var plays = f.MapPlays + (f.Offline ? 0 : 1);
        ProfileValue(w,
                     "PfHere",
                     "PfPlays",
                     f.MapStatsFetched ? HudFormat.Duration(tr, f.MapPlayTime + session) : "…",
                     f.MapStatsFetched ? tr.Format(plays == 1 ? HudTexts.PlaysOne : HudTexts.Plays, HudFormat.Count(plays)) : "");

        w.Text("PfTrackValue", "value", f.Track == 0 ? tr[HudTexts.Main] : tr.Format(HudTexts.BonusN, f.Track));
        w.Class("PfTrackPrev", "disabled", StepTrack(f.Track, -1) < 0);
        w.Class("PfTrackNext", "disabled", StepTrack(f.Track, 1) < 0);

        var loading = f.Offline && f.Records is null;
        var pb      = f.Offline ? OfflineRecord(f, f.Track, 0) : _recordModule.GetPlayerRecord(f.Target, f.Style, f.Track);
        var rank    = pb is null ? 0 : _recordModule.GetRankOfRecord(f.Style, f.Track, pb);
        ProfileValue(w,
                     "PfPb",
                     "PfPbRank",
                     loading ? "…" : pb is null ? tr[HudTexts.None] : HudFormat.FormatTime(pb.Time),
                     pb is null  ? ""
                     : rank == 1 ? tr[HudTexts.Sr]
                                   : tr.Format(HudTexts.RankOf, HudFormat.Count(rank), HudFormat.Count(_recordModule.GetTotalRecordCount(f.Style, f.Track))));
        w.Class("PfPb", "wr", rank == 1);

        // Bonuses and linear maps have no stages.
        var stages = f.Track == 0 ? Math.Min(StageCount(0), ProfileStageTiles) : 0;
        w.Class("PfStages", "Hidden", stages == 0);

        for (var i = 0; i < ProfileStageTiles; i++)
        {
            w.Class(ProfileStageIds[i], "Hidden", i >= stages);

            if (i >= stages)
            {
                continue;
            }

            var stagePb = f.Offline ? OfflineRecord(f, 0, i + 1) : _recordModule.GetPlayerRecord(f.Target, f.Style, 0, i + 1);
            w.Text(ProfileStageNameIds[i], "text", tr.Format(HudTexts.StageTile, i + 1));
            w.Text(ProfileStageTimeIds[i], "text", loading ? "…" : stagePb is null ? "—" : HudFormat.FormatTime(stagePb.Time));
            w.Class(ProfileStageTimeIds[i], "none", stagePb is null);
        }
    }
}
