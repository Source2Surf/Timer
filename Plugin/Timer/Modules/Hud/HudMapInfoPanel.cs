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
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Modules.MapInfo;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer.Modules;

// The map info card (!mapinfo / !mi): what the chat version printed, laid out like the profile card.
internal partial class HudModule
{
    private void OnMapInfoRequested(PlayerSlot slot)
    {
        if (_players[slot] is not { } p)
        {
            return;
        }

        // !mapinfo again closes it.
        if (p.MapInfo.Open)
        {
            CloseMapInfo(p);
        }
        else
        {
            OpenMapInfo(p);
        }
    }

    private void OpenMapInfo(HudPlayer p)
    {
        if (p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        p.Replays.Open = false;
        p.Profile.Open = false;
        CloseNominateMenu(p);
        CloseZonePanel(p);
        CloseRecords(p);
        CloseStyles(p);

        p.MapInfo.Open  = true;
        p.MapInfo.Dirty = true;
        p.MenuDirty     = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private static void CloseMapInfo(HudPlayer p)
    {
        if (!p.MapInfo.Open)
        {
            return;
        }

        p.MapInfo.Open = false;
        p.MenuDirty    = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void UpdateMapInfo(HudWriter w, HudPlayer p)
    {
        var m = p.MapInfo;
        w.Class("MiMenu", "Closed", !m.Open);

        if (!m.Open)
        {
            return;
        }

        // The SR and completions change with the boards.
        var version = _recordModule.RecordsVersion;

        if (version != m.Version)
        {
            m.Version = version;
            m.Dirty   = true;
        }

        if (!m.Dirty)
        {
            return;
        }

        m.Dirty = false;

        var tr      = p.Tr;
        var profile = _mapInfo.GetCurrentMapProfile();
        var mode    = _mapInfo.GetCurrentGameMode();
        w.Labels(HudLabels.MapInfo);

        // Header: name, tier and mode, and whether it's ranked.
        var tier = tr.Format(HudTexts.MiTier, profile.Tier[0]);
        w.Text("MiName", "text", _bridge.CurrentMapName);
        w.Text("MiSub", "text",
               mode switch
               {
                   EGameMode.Surf => ZString.Concat(tier, " · ", tr[HudTexts.MiSurf]),
                   EGameMode.Bhop => ZString.Concat(tier, " · ", tr[HudTexts.MiBhop]),
                   _              => tier,
               });
        w.Class("MiUnranked", "Hidden", profile.Ranked);

        // Layout: the main track's stages or checkpoints, and the bonuses with their tiers.
        var linear = _zoneModule.IsCurrentTrackLinear(0);
        ProfileValue(w, "MiLayout", "MiLayoutNote",
                     tr[linear ? HudTexts.ZoneLinear : HudTexts.MiStaged],
                     linear
                         ? tr.Format(HudTexts.MiCheckpoints, _zoneModule.GetCurrentTrackCheckpointCount(0))
                         : tr.Format(HudTexts.MiStages, _zoneModule.GetTotalStages(0)));

        var bonuses = Math.Clamp(profile.Bonuses, 0, Math.Min(profile.Tier.Length - 1, TimerConstants.MAX_TRACK - 1));
        var tiers   = new string[bonuses];

        for (var track = 1; track <= bonuses; track++)
        {
            tiers[track - 1] = tr.Format(HudTexts.TierN, profile.Tier[track]);
        }

        ProfileValue(w, "MiBonuses", "MiBonusesNote", bonuses > 0 ? bonuses.ToString(CultureInfo.InvariantCulture) : "—", string.Join(" · ", tiers));

        w.Class("MiBonuses", "none", bonuses == 0);

        // Records, on the player's own style.
        var style = _timerModule.GetTimerInfo(p.Slot)?.Style ?? 0;
        var sr    = _recordModule.GetWR(style, 0);
        var done  = _recordModule.GetTotalRecordCount(style, 0);
        w.Text("MiRecordsTitle", "text", tr.Format(HudTexts.MiRecords, _styleModule.GetStyleSetting(style).Name));
        ProfileValue(w, "MiSr", "MiSrNote", sr is null ? "—" : HudFormat.FormatTime(sr.Time), sr?.PlayerName ?? "");
        w.Class("MiSr", "wr", sr is not null);
        w.Class("MiSr", "none", sr is null);
        w.Text("MiDone", "text", done > 0 ? HudFormat.Count(done) : "—");
        w.Class("MiDone", "none", done == 0);

        // Activity. Dates are 0 for maps from before they were kept.
        w.Text("MiPlayed", "text", HudFormat.Count(profile.PlayCount));
        w.Text("MiTime", "text", HudFormat.Duration(tr, profile.TotalPlayTime));
        w.Text("MiAdded", "text", profile.AddedAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(profile.AddedAt).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "—");
        w.Class("MiAdded", "none", profile.AddedAt <= 0);
        w.Text("MiLast", "text", profile.LastPlayedAt > 0 ? HudFormat.Ago(tr, DateTimeOffset.FromUnixTimeMilliseconds(profile.LastPlayedAt).UtcDateTime, DateTime.UtcNow) : "—");
        w.Class("MiLast", "none", profile.LastPlayedAt <= 0);
    }

    private void ClickMapInfo(HudPlayer p, string buttonId)
    {
        if (!p.MapInfo.Open)
        {
            return;
        }

        if (buttonId == "MiClose")
        {
            CloseMapInfo(p);
        }
        else if (buttonId == "MiRecordsButton")
        {
            // This map's leaderboard; opening it closes this card.
            OpenRecords(p, null);
        }
    }
}
