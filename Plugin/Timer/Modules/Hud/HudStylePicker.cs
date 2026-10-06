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
using System.Text.RegularExpressions;
using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

// The style picker (!style): every enabled style with its description. A click picks one, a double-click (or Switch)
// switches to it; switching closes the menu.
internal partial class HudModule
{
    internal const int StyleRows = 6;

    private const float DoubleClickTime = 0.45f;

    internal static readonly string[] StyleRowIds  = StyleIds("");
    internal static readonly string[] StyleNameIds = StyleIds("Name");
    internal static readonly string[] StyleCmdIds  = StyleIds("Cmd");
    internal static readonly string[] StyleDescIds = StyleIds("Desc");

    // A label can't colour part of its text (custom HUDs refuse html labels), so the chat colour tags go.
    private static readonly Regex ColourTags = new (@"\{[A-Za-z]+\}", RegexOptions.Compiled);

    private static string[] StyleIds(string part)
        => Enumerable.Range(0, StyleRows).Select(i => ZString.Concat("StyRow", i, part)).ToArray();

    private void OnStyleMenuRequested(PlayerSlot slot)
    {
        if (_players[slot] is not { } p)
        {
            return;
        }

        // !style again closes it.
        if (p.Styles.Open)
        {
            CloseStyles(p);
        }
        else
        {
            OpenStyles(p);
        }
    }

    private void OpenStyles(HudPlayer p)
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

        // It opens on the player's style, picked.
        var m       = p.Styles;
        var current = CurrentStyle(p);
        var index   = Math.Max(0, IndexOfStyle(current));
        m.Picked    = current;
        m.Page      = index / StyleRows;
        m.LastClick = null;
        m.Open      = true;
        m.Dirty     = true;
        p.MenuDirty = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private static void CloseStyles(HudPlayer p)
    {
        if (!p.Styles.Open)
        {
            return;
        }

        p.Styles.Open = false;
        p.MenuDirty   = true;
        GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
    }

    private int CurrentStyle(HudPlayer p)
        => _timerModule.GetTimerInfo(p.Slot)?.Style ?? (_styleModule.GetStyleIds() is { Count: > 0 } ids ? ids[0] : 0);

    private int IndexOfStyle(int style)
    {
        var ids = _styleModule.GetStyleIds();

        for (var i = 0; i < ids.Count; i++)
        {
            if (ids[i] == style)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateStyles(HudWriter w, HudPlayer p)
    {
        var m = p.Styles;
        w.Class("StyMenu", "Closed", !m.Open);

        if (!m.Open || !m.Dirty)
        {
            return;
        }

        m.Dirty = false;

        var tr      = p.Tr;
        var ids     = _styleModule.GetStyleIds();
        var current = CurrentStyle(p);
        var pages   = HudFormat.PageCount(ids.Count, StyleRows);
        m.Page = Math.Clamp(m.Page, 0, pages - 1);

        w.Labels(HudLabels.Styles);
        w.Text("StyCount", "text", ids.Count == 1 ? tr[HudTexts.StylesOne] : tr.Format(HudTexts.StylesCount, ids.Count));

        for (var i = 0; i < StyleRows; i++)
        {
            var index = (m.Page * StyleRows) + i;
            var shown = index < ids.Count;
            w.Class(StyleRowIds[i], "blank", !shown);
            m.RowIds[i] = shown ? ids[index] : -1;

            if (!shown)
            {
                continue;
            }

            var id      = ids[index];
            var setting = _styleModule.GetStyleSetting(id);
            var cur     = id == current;
            w.Class(StyleRowIds[i], "cur", cur);
            w.Class(StyleRowIds[i], "sel", id == m.Picked && !cur);
            w.Text(StyleNameIds[i], "text", setting.Name);
            w.Text(StyleCmdIds[i], "text", cur ? tr[HudTexts.StyleCurrent] : ShortestCommand(setting.Command));
            w.Class(StyleCmdIds[i], "cur", cur);
            w.Text(StyleDescIds[i], "text", PlainDescription(setting.Description));
        }

        w.Text("StyPage", "text", tr.Format(HudTexts.Page, m.Page + 1, pages));
        w.Class("StyPrev", "disabled", m.Page == 0);
        w.Class("StyNext", "disabled", m.Page >= pages - 1);

        // Picking another style mid-run says what switching does.
        var other   = m.Picked != current && IndexOfStyle(m.Picked) >= 0;
        var running = _timerModule.GetTimerInfo(p.Slot)?.Status == ETimerStatus.Running;
        w.Text("StyNote", "text",
               other && running
                   ? tr.Format(HudTexts.StyleStopsRun, _styleModule.GetStyleSetting(m.Picked).Name)
                   : tr[HudTexts.StyleHint]);
        w.Class("StyNote", "warn", other && running);
        w.Class("StySwitch", "disabled", !other);
        w.Class("LStySwitch", "disabled", !other);
    }

    /// <summary>
    ///     The style's shortest chat command as typed: "sideways;sw" is "!sw".
    /// </summary>
    internal static string ShortestCommand(string commands)
    {
        var best = "";

        foreach (var command in commands.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (best.Length == 0 || command.Length < best.Length)
            {
                best = command;
            }
        }

        return best.Length == 0 ? "" : ZString.Concat('!', best);
    }

    internal static string PlainDescription(string description)
        => ColourTags.Replace(description, "").Trim();

    // ------------------------------------------------------------------ clicks

    private void ClickStyles(HudPlayer p, string buttonId)
    {
        var m = p.Styles;

        if (!m.Open)
        {
            return;
        }

        var now   = _bridge.GlobalVars.CurTime;
        var click = m.LastClick;
        m.LastClick = null;

        switch (buttonId)
        {
            case "StyClose":
                CloseStyles(p);

                return;
            case "StySwitch":
                SwitchToPicked(p);

                return;
            case "StyPrev" or "StyNext":
                m.Page += buttonId == "StyNext" ? 1 : -1;

                break;
            default:
                if (Array.IndexOf(StyleRowIds, buttonId) is var row and >= 0 && m.RowIds[row] is var id and >= 0)
                {
                    // The second click on the same row soon after switches.
                    if (click is { } last && last.Id == buttonId && now - last.At <= DoubleClickTime && id == m.Picked)
                    {
                        SwitchToPicked(p);

                        return;
                    }

                    m.Picked    = id;
                    m.LastClick = (buttonId, now);
                }

                break;
        }

        m.Dirty = true;
    }

    // Like the style's chat command (it stops the run and respawns); the menu closes.
    private void SwitchToPicked(HudPlayer p)
    {
        var m = p.Styles;

        if (m.Picked == CurrentStyle(p) || IndexOfStyle(m.Picked) < 0)
        {
            m.Dirty = true;

            return;
        }

        _styleModule.SwitchStyle(p.Slot, m.Picked);
        CloseStyles(p);
    }
}
