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

using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Timer.MapChooser;

/// <summary>
///     A chat text: its key in the timer's locale file, and the English shown when the key isn't there.
/// </summary>
internal sealed record ChooserText(string Key, string English);

/// <summary>
///     Every text the map chooser prints. The locale file (Plugin/Timer.Localization/locales/surftimer.json) carries
///     each of them; a test keeps the two in step.
/// </summary>
internal static class ChooserTexts
{
    public static readonly ChooserText AdminBusy        = new ("mc.admin.busy", "A vote is running or the map is already changing.");
    public static readonly ChooserText AdminNoVote      = new ("mc.admin.no_vote", "No vote is running.");
    public static readonly ChooserText Changing         = new ("mc.changing", "Changing to [lime]{0}[white] in {1} seconds.");
    public static readonly ChooserText Nextmap          = new ("mc.nextmap", "Next map: [lime]{0}[white].");
    public static readonly ChooserText NextmapPending   = new ("mc.nextmap.pending", "The next map hasn't been chosen yet.");
    public static readonly ChooserText NextmapSet       = new ("mc.nextmap.set", "The next map was set to [lime]{0}[white].");
    public static readonly ChooserText NominateChanged  = new ("mc.nominate.changed", "{0} changed their nomination to [lime]{1}[white].");
    public static readonly ChooserText NominateClosed   = new ("mc.nominate.closed", "Nominations are closed.");
    public static readonly ChooserText NominateCurrent  = new ("mc.nominate.current", "That's the current map.");
    public static readonly ChooserText NominateDone     = new ("mc.nominate.done", "{0} nominated [lime]{1}[white].");
    public static readonly ChooserText NominateFull     = new ("mc.nominate.full", "The nominations are full.");
    public static readonly ChooserText NominateMatches  = new ("mc.nominate.matches", "{0} maps match: {1}");
    public static readonly ChooserText NominateNoMatch  = new ("mc.nominate.no_match", "No map matches \"{0}\".");
    public static readonly ChooserText NominateNone     = new ("mc.nominate.none", "You haven't nominated a map.");
    public static readonly ChooserText NominateNotFound = new ("mc.nominate.not_found", "{0} isn't one of the maps.");
    public static readonly ChooserText NominateRecent   = new ("mc.nominate.recent", "[lime]{0}[white] was played recently.");
    public static readonly ChooserText NominateRemoved  = new ("mc.nominate.removed", "Your nomination of [lime]{0}[white] was removed.");
    public static readonly ChooserText NominateTaken    = new ("mc.nominate.taken", "[lime]{0}[white] is already nominated.");
    public static readonly ChooserText NominateUsage    = new ("mc.nominate.usage", "Pick a map in the menu, or type !nominate <name> or !nominate <tier>.");
    public static readonly ChooserText Nominations      = new ("mc.nominations", "Nominated: [lime]{0}[white]");
    public static readonly ChooserText NominationsNone  = new ("mc.nominations.none", "Nothing is nominated yet.");
    public static readonly ChooserText RtvAlready       = new ("mc.rtv.already", "You already want to rock the vote ({0}/{1}).");
    public static readonly ChooserText RtvChanging      = new ("mc.rtv.changing", "The map is already changing.");
    public static readonly ChooserText RtvNoMaps        = new ("mc.rtv.no_maps", "Rock the vote passed, but there are no other maps to vote for.");
    public static readonly ChooserText RtvRemoved       = new ("mc.rtv.removed", "You no longer want to rock the vote ({0}/{1}).");
    public static readonly ChooserText RtvTooSoon       = new ("mc.rtv.too_soon", "You can rock the vote in {0}.");
    public static readonly ChooserText RtvWants         = new ("mc.rtv.wants", "{0} wants to rock the vote ({1}/{2}).");
    public static readonly ChooserText Timeleft         = new ("mc.timeleft", "Time left on this map: {0}.");
    public static readonly ChooserText VoteCancelled    = new ("mc.vote.cancelled", "The vote was cancelled.");
    public static readonly ChooserText VoteCast         = new ("mc.vote.cast", "You voted for [lime]{0}[white].");
    public static readonly ChooserText VoteExtend       = new ("mc.vote.extend", "Extend {0} minutes");
    public static readonly ChooserText VoteExtended     = new ("mc.vote.extended", "The map was extended by {0} minutes.");
    public static readonly ChooserText VoteOption       = new ("mc.vote.option", "{0}. [lime]{1}[white]");
    public static readonly ChooserText VoteRunning      = new ("mc.vote.running", "A vote is already running.");
    public static readonly ChooserText VoteStarted      = new ("mc.vote.started", "Vote for the next map: type !1 to !{0} in chat, or use the vote keys.");
    public static readonly ChooserText VoteWon          = new ("mc.vote.won", "Next map: [lime]{0}[white] ({1} of {2} votes).");

    /// <summary>
    ///     All of them, for the test that checks the locale file.
    /// </summary>
    public static IReadOnlyList<ChooserText> All { get; } = typeof(ChooserTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                                .Where(x => x.FieldType == typeof(ChooserText))
                                                                                .Select(x => (ChooserText) x.GetValue(null)!)
                                                                                .ToList();
}
