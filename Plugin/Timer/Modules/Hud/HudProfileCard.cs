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
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

// The profile card (!profile, !profile name): a player's overall results for one style, then this map's for that
// style and a track. It's rebuilt on every refresh while open (PBs can change); the writer only sends changes.
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
            if (p.Profile.Open && p.Profile.Target == slot)
            {
                SetProfileOpen(p, false);
            }
            else
            {
                OpenProfile(p, slot);
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

        if (p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        p.Replays.Open = false;
        CloseNominateMenu(p);
        CloseZonePanel(p);
        CloseRecords(p);
        CloseStyles(p);

        var f = p.Profile;
        f.Open            = true;
        f.Version++;
        f.Target          = target;
        f.SteamId         = (ulong) client.SteamId;
        f.Rank            = 0;
        f.RankOf          = 0;
        f.Summary         = null;
        f.SummaryFetched  = false;
        f.MapPlayTime     = 0;
        f.MapPlays        = 0;
        f.MapStatsFetched = false;

        // It starts on what they're playing.
        var info = _timerModule.GetTimerInfo(target);
        f.Style = info?.Style ?? 0;
        f.Track = info?.Track ?? 0;

        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);

        var version = f.Version;
        var steamId = client.SteamId;
        var mapName = _bridge.CurrentMapName;

        FetchForProfile(p, version, "GetPlayerPointsRank", () => _request.GetPlayerPointsRank(steamId),
                        (profile, rank) => (profile.Rank, profile.RankOf) = rank);
        FetchForProfile(p, version, "GetPlayerSummary", () => _request.GetPlayerSummary(steamId),
                        (profile, summary) => (profile.Summary, profile.SummaryFetched) = (summary, true),
                        profile => profile.SummaryFetched = true);
        FetchForProfile(p, version, "GetPlayerMapStatsAsync", () => _request.GetPlayerMapStatsAsync(steamId, mapName),
                        (profile, stats) => (profile.MapPlayTime, profile.MapPlays, profile.MapStatsFetched) = (stats.playTime, stats.playCount, true),
                        profile => profile.MapStatsFetched = true);
    }

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
            OpenProfile(p, p.Profile.Target);

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

        // They left: nothing more to show.
        if (_bridge.ClientManager.GetGameClient(f.Target) is not { } client || (ulong) client.SteamId != f.SteamId)
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
        var profile   = _playerManager.GetPlayerProfile(f.Target);
        var session   = _recordModule.GetSessionTime(f.Target); // this visit, which the stored play time doesn't have yet

        var tr = p.Tr;
        w.Labels(HudLabels.Profile);

        // Header
        w.Text("PfName", "text", client.Name);
        w.Text("PfRank", "text",
               profile is null ? ""
               : f.RankOf > 0  ? tr.Format(HudTexts.ProfileRank, HudFormat.Count(f.Rank), HudFormat.Count(f.RankOf), HudFormat.Count(profile.Points))
                                 : tr.Format(HudTexts.ProfilePoints, HudFormat.Count(profile.Points)));
        w.Text("PfJoined", "text",
               ZString.Concat(profile is null ? "" : tr.Format(HudTexts.Joined, profile.JoinDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                              f.Summary is { } played ? tr.Format(HudTexts.Played, HudFormat.Duration(tr, played.PlayTime + session)) : ""));

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

        // This map: the time here is the whole map's; the PB and stages follow the style and track.
        ProfileValue(w,
                     "PfHere",
                     "PfPlays",
                     f.MapStatsFetched ? HudFormat.Duration(tr, f.MapPlayTime + session) : "…",
                     f.MapStatsFetched ? tr.Format(f.MapPlays == 0 ? HudTexts.PlaysOne : HudTexts.Plays, HudFormat.Count(f.MapPlays + 1)) : "");

        w.Text("PfTrackValue", "value", f.Track == 0 ? tr[HudTexts.Main] : tr.Format(HudTexts.BonusN, f.Track));
        w.Class("PfTrackPrev", "disabled", StepTrack(f.Track, -1) < 0);
        w.Class("PfTrackNext", "disabled", StepTrack(f.Track, 1) < 0);

        var pb   = _recordModule.GetPlayerRecord(f.Target, f.Style, f.Track);
        var rank = pb is null ? 0 : _recordModule.GetRankForTime(f.Style, f.Track, pb.Time);
        ProfileValue(w,
                     "PfPb",
                     "PfPbRank",
                     pb is null ? tr[HudTexts.None] : HudFormat.FormatTime(pb.Time),
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

            var stagePb = _recordModule.GetPlayerRecord(f.Target, f.Style, 0, i + 1);
            w.Text(ProfileStageNameIds[i], "text", tr.Format(HudTexts.StageTile, i + 1));
            w.Text(ProfileStageTimeIds[i], "text", stagePb is null ? "—" : HudFormat.FormatTime(stagePb.Time));
            w.Class(ProfileStageTimeIds[i], "none", stagePb is null);
        }
    }
}
