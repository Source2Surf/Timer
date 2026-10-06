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
using System.Linq;
using Cysharp.Text;
using Sharp.Shared.Objects;
using Source2Surf.Timer.Modules.Hud;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules;

// The map chooser (Timer.MapChooser, through IMapChooser): its vote panel, which never takes the cursor so players vote
// mid-run, and its nominate menu, which does. The chooser keeps all the state; the HUD redraws when its version moves.
internal partial class HudModule
{
    internal const int VoteRows     = 9; // up to 8 maps plus extend, the numbers 1-9 in chat
    internal const int NominateRows = 8;

    internal static readonly string[] VoteRowIds   = Enumerable.Range(0, VoteRows).Select(i => ZString.Concat("McVote", i)).ToArray();
    internal static readonly string[] VoteBarIds   = VoteIds("Bar");
    internal static readonly string[] VoteNumIds   = VoteIds("Num");
    internal static readonly string[] VoteMapIds   = VoteIds("Map");
    internal static readonly string[] VoteTickIds  = VoteIds("Tick");
    internal static readonly string[] VoteTierIds  = VoteIds("Tier");
    internal static readonly string[] VoteCountIds = VoteIds("Count");

    internal static readonly string[] NominateRowIds  = Enumerable.Range(0, NominateRows).Select(i => ZString.Concat("McNom", i)).ToArray();
    internal static readonly string[] NominateNameIds = NominateIds("Name");
    internal static readonly string[] NominateTierIds = NominateIds("Tier");
    internal static readonly string[] NominatePbIds   = NominateIds("Pb");
    internal static readonly string[] NominateTagIds  = NominateIds("Tag");

    internal static readonly string[] TierChipIds   = Enumerable.Range(0, 9).Select(i => ZString.Concat("McTier", i)).ToArray();
    internal static readonly string[] TierLabelIds  = Enumerable.Range(0, 9).Select(i => ZString.Concat("NomTier", i)).ToArray();
    private static readonly string[]  Digits        = Enumerable.Range(0, 11).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
    private static readonly string[]  VoteCaps      = ["VoteKeyUp", "VoteKeyDown", "VoteKeySelect"];

    private static string[] VoteIds(string part)
        => Enumerable.Range(0, VoteRows).Select(i => ZString.Concat("Vote", i, part)).ToArray();

    private static string[] NominateIds(string part)
        => Enumerable.Range(0, NominateRows).Select(i => ZString.Concat("Nom", i, part)).ToArray();

    private void UpdateMapChooser(HudWriter w, HudPlayer p, float now)
    {
        var c       = p.Chooser;
        var version = _mapChooser.Version;
        var changed = version != c.Version;

        if (changed)
        {
            c.Version = version;
            SyncNominateMenu(p, _mapChooser.GetNominateMenu(p.Slot));
        }

        UpdateVotePanel(w, p, now, changed);

        w.Class("NMenu", "Closed", c.Menu is null);

        if (c.Menu is not null && (changed || c.Dirty))
        {
            c.Dirty = false;
            UpdateNominateMenu(w, p, c.Menu);
        }
    }

    // ------------------------------------------------------------------ round timer

    // The map's time left runs on the game's own round timer. The round never ends on time
    // (mp_ignore_round_win_conditions), and a round restart puts the time back, so this keeps it in line every frame.
    private void SyncRoundTime(IGameRules rules, float now)
    {
        var left = _mapChooser.TimeLeft;

        if (left <= 0 || rules.IsWarmupPeriod)
        {
            return;
        }

        var want = now + left - rules.RoundStartTime;

        // Rewritten only when it's a second out, so it isn't sent every frame.
        if (MathF.Abs(want - rules.RoundTime) >= 1)
        {
            rules.RoundTime = (int) MathF.Ceiling(want);
        }
    }

    // ------------------------------------------------------------------ vote

    private void UpdateVotePanel(HudWriter w, HudPlayer p, float now, bool changed)
    {
        var c     = p.Chooser;
        var vote  = _mapChooser.Vote;
        var shown = vote is not null;
        w.Class("VotePanel", "Hidden", !shown);

        // The client looks a key up only when the label's text changes: a fresh nonce each time it shows.
        if (shown && !c.VoteShown)
        {
            c.VoteRecheck = !c.VoteRecheck;
        }

        c.VoteShown = shown;

        if (vote is null)
        {
            return;
        }

        var tr     = p.Tr;
        var second = HudFormat.WholeSeconds(vote.EndsAt - now);

        if (second != c.VoteSecond)
        {
            c.VoteSecond = second;
            w.Text("VoteTime", "text", HudFormat.Countdown(second));
        }

        if (!changed)
        {
            return;
        }

        w.Labels(HudLabels.Vote);
        w.Text("VoteTitle", "text", tr[vote.Kind == MapVoteKind.RockTheVote ? HudTexts.VoteRtv : HudTexts.VoteEndOfMap]);

        var options = vote.Options;
        var counts  = vote.Counts;
        var total   = 0;

        for (var i = 0; i < counts.Count; i++)
        {
            total += counts[i];
        }

        var cursor = _mapChooser.GetVoteCursor(p.Slot);
        var choice = _mapChooser.GetVoteChoice(p.Slot);

        for (var i = 0; i < VoteRows; i++)
        {
            var on = i < options.Count;
            w.Class(VoteRowIds[i], "Hidden", !on);

            if (!on)
            {
                continue;
            }

            var option = options[i];
            var count  = i < counts.Count ? counts[i] : 0;

            w.Text(VoteNumIds[i], "text", Digits[i + 1]);
            w.Text(VoteMapIds[i], "text", option.IsExtend ? tr.Format(HudTexts.VoteExtend, option.ExtendMinutes) : option.Map);
            w.Class(VoteMapIds[i], "ext", option.IsExtend);
            w.Text(VoteTierIds[i], "text", option.IsExtend || option.Tier == 0 ? "" : tr.Format(HudTexts.TierN, option.Tier));
            w.Text(VoteCountIds[i], "text", count.ToString(CultureInfo.InvariantCulture));
            w.Numbered(VoteBarIds[i], "vb", Digits[HudFormat.ShareStep(count, total)]);
            w.Class(VoteBarIds[i], "mine", i == choice);
            w.Class(VoteTickIds[i], "Hidden", i != choice);
            w.Class(VoteRowIds[i], "cur", i == cursor);
        }

        var keys = _mapChooser.VoteKeys;
        w.Text("VoteKeyUp", "editkey", ZString.Concat('%', keys.Up, '%'));
        w.Text("VoteKeyDown", "editkey", ZString.Concat('%', keys.Down, '%'));
        w.Text("VoteKeySelect", "editkey", ZString.Concat('%', keys.Select, '%'));

        var nonce = c.VoteRecheck ? LocsNonce : "";

        foreach (var cap in VoteCaps)
        {
            w.Text(cap, "nonce", nonce);
        }

        w.Text("VoteChat", "text", tr.Format(HudTexts.VoteChat, 1, options.Count));
        w.Text("VoteNote", "text", tr[HudTexts.VoteNote]);
        w.Text("VoteNoteExample", "text", ZString.Concat("bind f3 ", keys.Up));
        w.Text("VoteCmdUp", "text", keys.Up);
        w.Text("VoteCmdDown", "text", keys.Down);
        w.Text("VoteCmdVote", "text", keys.Select);
        w.Text("VoteNoteUp", "text", tr[HudTexts.VoteNoteUp]);
        w.Text("VoteNoteDown", "text", tr[HudTexts.VoteNoteDown]);
        w.Text("VoteNoteVote", "text", tr[HudTexts.VoteNoteVote]);
    }

    // ------------------------------------------------------------------ nominate

    // The chooser opens and closes the menu (!nominate, a nomination, its Close); this follows it.
    private void SyncNominateMenu(HudPlayer p, NominateMenu? menu)
    {
        var c   = p.Chooser;
        var was = c.Menu;

        if (menu is not null && was is null)
        {
            // It shares the other menus' spot.
            if (p.MenuOpen)
            {
                SetMenuOpen(p, false);
            }

            p.Replays.Open = false;
            p.Profile.Open = false;
            CloseZonePanel(p);
            CloseRecords(p);
            CloseStyles(p);
            CloseMapInfo(p);
            c.Page         = 0;
            c.Note         = null;
        }
        else if (menu is not null && was is not null
                 && (menu.Tier != was.Tier || menu.UnfinishedOnly != was.UnfinishedOnly || menu.Search != was.Search))
        {
            c.Page = 0;
            c.Note = null;
        }

        c.Menu = menu;

        if ((menu is null) != (was is null))
        {
            p.MenuDirty = true;
            GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
        }
    }

    // Another menu opening closes this one.
    private void CloseNominateMenu(HudPlayer p)
    {
        if (p.Chooser.Menu is null)
        {
            return;
        }

        p.Chooser.Menu = null;
        _mapChooser.CloseNominateMenu(p.Slot);
    }

    private static void UpdateNominateMenu(HudWriter w, HudPlayer p, NominateMenu menu)
    {
        var c       = p.Chooser;
        var tr      = p.Tr;
        var entries = menu.Entries;
        var pages   = HudFormat.PageCount(entries.Count, NominateRows);
        c.Page = Math.Clamp(c.Page, 0, pages - 1);

        w.Labels(HudLabels.Nominate);
        w.Text("NomCount", "text", entries.Count == 1 ? tr[HudTexts.NomCountOne] : tr.Format(HudTexts.NomCount, entries.Count));

        for (var tier = 0; tier < TierChipIds.Length; tier++)
        {
            w.Text(TierLabelIds[tier], "text", tier == 0 ? tr[HudTexts.NomAll] : tr.Format(HudTexts.TierN, tier));
            w.Class(TierChipIds[tier], "on", menu.Tier == tier);
            w.Class(TierLabelIds[tier], "on", menu.Tier == tier);
        }

        w.Class("NomCheck", "on", menu.UnfinishedOnly);
        w.Class("NomSearch", "Hidden", string.IsNullOrEmpty(menu.Search));

        if (!string.IsNullOrEmpty(menu.Search))
        {
            w.Text("NomSearch", "text", tr.Format(HudTexts.NomSearch, menu.Search));
        }

        for (var i = 0; i < NominateRows; i++)
        {
            var index = (c.Page * NominateRows) + i;
            var entry = index < entries.Count ? entries[index] : null;
            w.Class(NominateRowIds[i], "blank", entry is null);

            if (entry is null)
            {
                continue;
            }

            var off = entry.State is NominateState.Recent or NominateState.Current;
            w.Text(NominateNameIds[i], "text", entry.Map);
            w.Text(NominateTierIds[i], "text", entry.Tier == 0 ? "" : tr.Format(HudTexts.TierN, entry.Tier));
            w.Text(NominatePbIds[i], "text", entry.PersonalBest is { } best ? HudFormat.FormatTime(best) : "—");
            w.Class(NominatePbIds[i], "none", entry.PersonalBest is null);
            w.Class(NominateNameIds[i], "off", off);
            w.Class(NominateTierIds[i], "off", off);
            w.Class(NominatePbIds[i], "off", off);

            w.Text(NominateTagIds[i],
                   "text",
                   entry.State switch
                   {
                       NominateState.Mine      => tr[HudTexts.NomMine],
                       NominateState.Nominated => tr[HudTexts.NomNominated],
                       NominateState.Recent    => tr[HudTexts.NomRecent],
                       NominateState.Current   => tr[HudTexts.NomCurrent],
                       _                       => "",
                   });

            w.Class(NominateTagIds[i], "mine", entry.State == NominateState.Mine);
            w.Class(NominateTagIds[i], "nom", entry.State == NominateState.Nominated);
        }

        w.Class("NomEmpty", "Hidden", entries.Count > 0);

        if (entries.Count == 0)
        {
            w.Text("NomEmpty", "text", tr[HudTexts.NomEmpty]);
        }

        w.Text("NomPage", "text", tr.Format(HudTexts.Page, c.Page + 1, pages));
        w.Class("McNomPrev", "disabled", c.Page == 0);
        w.Class("McNomNext", "disabled", c.Page >= pages - 1);
        w.Text("NomNote", "text", c.Note ?? tr[HudTexts.NomHint]);
        w.Class("NomNote", "warn", c.Note is not null);
    }

    // ------------------------------------------------------------------ clicks

    private void ClickMapChooser(HudPlayer p, string buttonId)
    {
        // A vote option can be clicked when a menu already has the cursor.
        if (TryParseIndex(buttonId, "McVote", out var option))
        {
            _mapChooser.CastVote(p.Slot, option);

            return;
        }

        var c = p.Chooser;

        if (c.Menu is not { } menu)
        {
            return;
        }

        if (buttonId == "McNomClose")
        {
            CloseNominateMenu(p);
            GetLayout(p)?.SetInputCaptureEnabled(p.Slot, p.AnyMenuOpen);
        }
        else if (buttonId is "McNomPrev" or "McNomNext")
        {
            c.Page  += buttonId == "McNomNext" ? 1 : -1;
            c.Dirty =  true;
        }
        else if (buttonId == "McUnfinished")
        {
            _mapChooser.SetNominateFilter(p.Slot, menu.Tier, !menu.UnfinishedOnly);
        }
        else if (TryParseIndex(buttonId, "McTier", out var tier))
        {
            _mapChooser.SetNominateFilter(p.Slot, tier, menu.UnfinishedOnly);
        }
        else if (TryParseIndex(buttonId, "McNom", out var row) && (c.Page * NominateRows) + row is var index && index < menu.Entries.Count)
        {
            var map = menu.Entries[index].Map;
            var tr  = p.Tr;

            // On success the chooser closes the menu and says so in chat.
            c.Note = _mapChooser.Nominate(p.Slot, map) switch
            {
                NominateResult.AlreadyNominated => tr.Format(HudTexts.NomAlready, map),
                NominateResult.Full             => tr[HudTexts.NomFull],
                NominateResult.CurrentMap       => tr.Format(HudTexts.NomCurrentMap, map),
                NominateResult.Recent           => tr.Format(HudTexts.NomRecentMap, map),
                NominateResult.NotFound         => tr.Format(HudTexts.NomNotFound, map),
                NominateResult.Closed           => tr[HudTexts.NomClosed],
                _                               => null,
            };

            c.Dirty = true;
        }
    }

    private static bool TryParseIndex(string buttonId, string prefix, out int index)
    {
        index = -1;

        return buttonId.StartsWith(prefix, StringComparison.Ordinal)
               && int.TryParse(buttonId.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }
}
