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
using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Modules;

// The admin zone panel (!zone): list, delete and add zones. Placing takes aim, so the panel steps aside for a prompt
// while a zone is placed, and comes back when one placed from it is done. The zone module keeps all the state.
internal partial class HudModule
{
    internal const int ZoneRows = 8;

    internal static readonly string[] ZoneRowIds   = Enumerable.Range(0, ZoneRows).Select(i => ZString.Concat("ZnRow", i)).ToArray();
    internal static readonly string[] ZoneNameIds  = Enumerable.Range(0, ZoneRows).Select(i => ZString.Concat("ZnRow", i, "Name")).ToArray();
    internal static readonly string[] ZoneMapIds   = Enumerable.Range(0, ZoneRows).Select(i => ZString.Concat("ZnRow", i, "Map")).ToArray();
    internal static readonly string[] ZoneDelIds   = Enumerable.Range(0, ZoneRows).Select(i => ZString.Concat("ZnDel", i)).ToArray();
    internal static readonly string[] ZoneDelLabel = Enumerable.Range(0, ZoneRows).Select(i => ZString.Concat("ZnDel", i, "Label")).ToArray();

    // The type chips, in EZoneType order.
    internal static readonly EZoneType[] ZoneTypes     = [EZoneType.Start, EZoneType.End, EZoneType.Stage, EZoneType.Checkpoint, EZoneType.StopTimer];
    internal static readonly string[]    ZoneTypeIds   = Enumerable.Range(0, ZoneTypes.Length).Select(i => ZString.Concat("ZnType", i)).ToArray();
    internal static readonly string[]    ZoneTypeLabel = Enumerable.Range(0, ZoneTypes.Length).Select(i => ZString.Concat("ZnType", i, "Label")).ToArray();

    private void OnZoneEditorRequested(PlayerSlot slot)
    {
        // !zone again closes it.
        if (_players[slot] is { } p)
        {
            if (p.Zones.Open)
            {
                CloseZonePanel(p);
            }
            else
            {
                OpenZonePanel(p);
            }
        }
    }

    private void OpenZonePanel(HudPlayer p)
    {
        var z = p.Zones;

        if (z.Open)
        {
            return;
        }

        if (p.MenuOpen)
        {
            SetMenuOpen(p, false);
        }

        p.Replays.Open = false;
        p.Profile.Open = false;
        CloseNominateMenu(p);

        if (z.Number == 0 && ZoneEdit.IsNumbered(z.Type))
        {
            z.Number = _zoneModule.NextZoneNumber(z.Track, z.Type);
        }

        z.Open    = true;
        z.Confirm = null;
        z.Dirty   = true;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    // Closing ends the editing session, unless a zone is still being placed: the panel comes back when it's done.
    private void CloseZonePanel(HudPlayer p)
    {
        var z = p.Zones;

        if (!z.Open)
        {
            return;
        }

        z.Open    = false;
        z.Confirm = null;
        z.Note    = null;

        if (z.Build is null)
        {
            z.FromPanel = false;
            _zoneModule.CloseZoneEditor(p.Slot);
        }

        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private void UpdateZones(HudWriter w, HudPlayer p)
    {
        var z       = p.Zones;
        var version = _zoneModule.EditVersion;

        var changed = version != z.Version;

        if (changed)
        {
            z.Version = version;
            SyncZones(p);
            z.Dirty = true;
        }

        w.Class("ZnMenu", "Closed", !z.Open);
        UpdateZonePrompt(w, p, changed);

        if (z.Open && z.Dirty)
        {
            z.Dirty = false;
            UpdateZoneMenu(w, p);
        }
    }

    // A zone placed from the panel brings the panel back, with the new zone picked out.
    private void SyncZones(HudPlayer p)
    {
        var z     = p.Zones;
        var was   = z.Build;
        var known = z.Known;

        z.Build = _zoneModule.GetZoneBuild(p.Slot);

        // Only editors look at the list.
        if (!_zoneModule.IsZoneEditor(p.Slot))
        {
            known.Clear();
            z.KnownValid = false;

            return;
        }

        var zones = _zoneModule.GetZones();
        var added = (uint?) null;

        foreach (var entry in zones)
        {
            if (z.KnownValid && !known.Contains(entry.Id) && was is { } b && entry.Track == b.Track && entry.Type == b.Type && entry.Number == b.Number)
            {
                added = entry.Id;
            }
        }

        known.Clear();

        foreach (var entry in zones)
        {
            known.Add(entry.Id);
        }

        z.KnownValid = true;

        if (was is { } done && z.Build is null)
        {
            if (added is { } id && z.FromPanel)
            {
                z.Added = id;
                z.Track = done.Track;
                z.Page  = Math.Max(0, IndexOf(zones, done.Track, id)) / ZoneRows;
                z.Note  = p.Tr.Format(HudTexts.ZnAdded, ZoneName(p, done.Type, done.Number), TrackName(p, done.Track));
                z.Warn  = false;
                z.Number = ZoneEdit.IsNumbered(z.Type) ? _zoneModule.NextZoneNumber(z.Track, z.Type) : 0;
                OpenZonePanel(p);
            }

            z.FromPanel = false;
        }
    }

    private static int IndexOf(IReadOnlyList<ZoneEntry> zones, int track, uint id)
    {
        var i = 0;

        foreach (var entry in zones)
        {
            if (entry.Track != track)
            {
                continue;
            }

            if (entry.Id == id)
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    // The prompt while a zone is being placed, from the panel or typed: no cursor, so the admin can aim.
    private static void UpdateZonePrompt(HudWriter w, HudPlayer p, bool changed)
    {
        var z     = p.Zones;
        var shown = z.Build is not null && !z.Open;
        w.Class("ZnPrompt", "Hidden", !shown);

        // The use key is looked up when the cap's text changes: a fresh nonce each time the prompt shows.
        var appeared = shown && !z.PromptShown;

        if (appeared)
        {
            z.PromptRecheck = !z.PromptRecheck;
        }

        z.PromptShown = shown;

        if (z.Build is not { } b || !(changed || appeared))
        {
            return;
        }

        var tr     = p.Tr;
        var second = b.Step >= 1;
        w.Labels(HudLabels.ZonePrompt);
        w.Text("ZnPromptTitle", "text", tr.Format(HudTexts.ZnPlacing, ZoneName(p, b.Type, b.Number)));
        w.Text("ZnPromptTrack", "text", TrackName(p, b.Track));
        w.Text("ZnStep", "text", tr.Format(HudTexts.ZnCorner, second ? 2 : 1));
        w.Text("ZnDot1", "text", second ? "✓" : "1");
        w.Class("ZnDot1", "on", !second);
        w.Class("ZnDot1", "done", second);
        w.Class("ZnDot2", "on", second);
        w.Text("ZnPromptDo", "text", tr[second ? HudTexts.ZnAimSecond : HudTexts.ZnAimFirst]);
        w.Text("ZnPromptKey", "editkey", "%+use%");
        w.Text("ZnPromptKey", "nonce", z.PromptRecheck ? LocsNonce : "");
        w.Class("LZnTall", "Hidden", !second);
    }

    private void UpdateZoneMenu(HudWriter w, HudPlayer p)
    {
        var z     = p.Zones;
        var tr    = p.Tr;
        var zones = _zoneModule.GetZones();
        var count = 0;

        foreach (var entry in zones)
        {
            if (entry.Track == z.Track)
            {
                count++;
            }
        }

        var pages = HudFormat.PageCount(count, ZoneRows);
        z.Page = Math.Clamp(z.Page, 0, pages - 1);

        w.Labels(HudLabels.Zones);
        w.Text("ZnCount", "text", count == 1 ? tr[HudTexts.ZnCountOne] : tr.Format(HudTexts.ZnCount, count));
        w.Text("ZnTrackValue", "value", TrackName(p, z.Track));
        w.Class("ZnTrackPrev", "disabled", z.Track == 0);
        w.Class("ZnTrackNext", "disabled", z.Track >= TimerConstants.MAX_TRACK - 1);

        // This track's zones, a page of them.
        var first = z.Page * ZoneRows;
        var index = 0;
        var row   = 0;

        foreach (var entry in zones)
        {
            if (entry.Track != z.Track)
            {
                continue;
            }

            if (index++ < first || row >= ZoneRows)
            {
                continue;
            }

            var confirm = z.Confirm == entry.Id;
            w.Class(ZoneRowIds[row], "blank", false);
            w.Class(ZoneRowIds[row], "new", z.Added == entry.Id);
            w.Text(ZoneNameIds[row], "text", ZoneName(p, entry.Type, entry.Number));
            w.Class(ZoneNameIds[row], "map", entry.Prebuilt);
            w.Class(ZoneMapIds[row], "Hidden", !entry.Prebuilt);
            w.Class(ZoneDelIds[row], "Hidden", entry.Prebuilt);
            w.Class(ZoneDelIds[row], "confirm", confirm);
            w.Text(ZoneDelLabel[row], "text", tr[confirm ? HudTexts.ZnConfirm : HudTexts.ZnDelete]);
            z.RowIds[row] = entry.Id;
            row++;
        }

        for (; row < ZoneRows; row++)
        {
            w.Class(ZoneRowIds[row], "blank", true);
            w.Class(ZoneMapIds[row], "Hidden", true);
            w.Class(ZoneDelIds[row], "Hidden", true);
            z.RowIds[row] = 0;
        }

        w.Class("ZnEmpty", "Hidden", count > 0);
        w.Text("ZnPage", "text", tr.Format(HudTexts.Page, z.Page + 1, pages));
        w.Class("ZnPrev", "disabled", z.Page == 0);
        w.Class("ZnNext", "disabled", z.Page >= pages - 1);

        // Add a zone, or, while one is being placed, what's being placed.
        var placing = z.Build is not null;
        w.Class("ZnAdd", "Hidden", placing);
        w.Class("ZnStatus", "Hidden", !placing);

        string? note = z.Note;
        var     warn = z.Warn;

        if (z.Build is { } build)
        {
            w.Text("ZnStatusName", "text", ZString.Concat(ZoneName(p, build.Type, build.Number), " · ", TrackName(p, build.Track)));
            w.Text("ZnStatusSub", "text", tr.Format(HudTexts.ZnStatus, build.Step + 1));
            w.Text("ZnPlaceLabel", "text", tr[HudTexts.ZnCancel]);
        }
        else
        {
            var numbered = ZoneEdit.IsNumbered(z.Type);

            for (var i = 0; i < ZoneTypes.Length; i++)
            {
                w.Class(ZoneTypeIds[i], "on", ZoneTypes[i] == z.Type);
                w.Class(ZoneTypeLabel[i], "on", ZoneTypes[i] == z.Type);
            }

            w.Text("ZnNumberValue", "value", numbered ? z.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—");
            w.Class("ZnNumPrev", "disabled", !numbered || z.Number <= ZoneEdit.FirstNumber(z.Type));
            w.Class("ZnNumNext", "disabled", !numbered || z.Number >= ZoneEdit.MaxNumber);

            var name = ZoneName(p, z.Type, z.Number);
            w.Text("ZnPlaceLabel", "text", tr.Format(HudTexts.ZnPlace, name));

            // A taken number (or a second Start/End) adds another area that counts as the same zone.
            if (note is null && zones.Any(x => x.Track == z.Track && x.Type == z.Type && x.Number == (numbered ? z.Number : 0)))
            {
                note = tr.Format(HudTexts.ZnTaken, TrackName(p, z.Track), name);
                warn = true;
            }
        }

        w.Text("ZnNote", "text", note ?? tr[HudTexts.ZnHint]);
        w.Class("ZnNote", "warn", note is not null && warn);
    }

    private static string TrackName(HudPlayer p, int track)
        => track == 0 ? p.Tr[HudTexts.Main] : p.Tr.Format(HudTexts.BonusN, track);

    private static string ZoneName(HudPlayer p, EZoneType type, int number)
        => type switch
        {
            EZoneType.Start      => p.Tr[HudTexts.ZnStart],
            EZoneType.End        => p.Tr[HudTexts.ZnEnd],
            EZoneType.Stage      => p.Tr.Format(HudTexts.ZnStageN, number),
            EZoneType.Checkpoint => p.Tr.Format(HudTexts.ZnCheckpointN, number),
            _                    => p.Tr[HudTexts.ZnStopTimer],
        };

    // ------------------------------------------------------------------ clicks

    private void ClickZonePanel(HudPlayer p, string buttonId)
    {
        var z = p.Zones;

        if (!z.Open)
        {
            return;
        }

        var keepConfirm = false;

        switch (buttonId)
        {
            case "ZnClose":
                // While placing, closing goes back to it.
                CloseZonePanel(p);

                return;
            case "ZnTrackPrev" or "ZnTrackNext":
                z.Track  = Math.Clamp(z.Track + (buttonId == "ZnTrackNext" ? 1 : -1), 0, TimerConstants.MAX_TRACK - 1);
                z.Page   = 0;
                z.Number = ZoneEdit.IsNumbered(z.Type) ? _zoneModule.NextZoneNumber(z.Track, z.Type) : 0;
                z.Note   = null;

                break;
            case "ZnPrev" or "ZnNext":
                z.Page += buttonId == "ZnNext" ? 1 : -1;

                break;
            case "ZnNumPrev" or "ZnNumNext":
                if (ZoneEdit.IsNumbered(z.Type))
                {
                    z.Number = Math.Clamp(z.Number + (buttonId == "ZnNumNext" ? 1 : -1), ZoneEdit.FirstNumber(z.Type), ZoneEdit.MaxNumber);
                    z.Note   = null;
                }

                break;
            case "ZnPlace":
                PlaceZone(p);

                return;
            default:
                if (TryParseIndex(buttonId, "ZnType", out var type) && type < ZoneTypes.Length)
                {
                    z.Type   = ZoneTypes[type];
                    z.Number = ZoneEdit.IsNumbered(z.Type) ? _zoneModule.NextZoneNumber(z.Track, z.Type) : 0;
                    z.Note   = null;
                }
                else if (TryParseIndex(buttonId, "ZnDel", out var row) && row < ZoneRows && z.RowIds[row] is var id and not 0)
                {
                    keepConfirm = DeleteZone(p, id);
                }

                break;
        }

        if (!keepConfirm)
        {
            z.Confirm = null;
        }

        z.Dirty = true;
    }

    // The first click asks, the second deletes. Returns whether it's waiting for the second.
    private bool DeleteZone(HudPlayer p, uint id)
    {
        var z = p.Zones;

        if (_zoneModule.GetZones().FirstOrDefault(x => x.Id == id) is not { Id: not 0 } entry || entry.Prebuilt)
        {
            return false;
        }

        var name = ZoneName(p, entry.Type, entry.Number);

        if (z.Confirm != id)
        {
            z.Confirm = id;
            z.Note    = p.Tr.Format(HudTexts.ZnConfirmAsk, name);
            z.Warn    = true;

            return true;
        }

        z.Note = p.Tr.Format(_zoneModule.DeleteZone(p.Slot, id) ? HudTexts.ZnDeleted : HudTexts.ZnDeleteFailed, name);
        z.Warn = false;

        return false;
    }

    private void PlaceZone(HudPlayer p)
    {
        var z = p.Zones;

        // While placing, this button cancels it.
        if (z.Build is not null)
        {
            _zoneModule.CancelZoneBuild(p.Slot);
            z.FromPanel = false;
            z.Note      = p.Tr[HudTexts.ZnCancelled];
            z.Warn      = false;
            z.Dirty     = true;

            return;
        }

        var number = ZoneEdit.IsNumbered(z.Type) ? z.Number : 0;

        if (!_zoneModule.StartZoneBuild(p.Slot, z.Track, z.Type, number))
        {
            z.Note  = p.Tr[HudTexts.ZnStartFailed];
            z.Warn  = true;
            z.Dirty = true;

            return;
        }

        // The panel steps aside so the admin can aim; it comes back when the zone is placed.
        z.FromPanel = true;
        z.Added     = null;
        z.Note      = null;
        z.Open      = false;
        z.Confirm   = null;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }
}
