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
using System.Reflection;
using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Localization;

/// <summary>
///     A chat text: its key for the localization provider, and its English, which players read when the provider has
///     nothing for them. {0}, {1}… mark where values go.
/// </summary>
internal sealed record ChatText(string Key, string English);

/// <summary>
///     The chat texts one player reads: in their language where the localization provider has them, else English. A
///     translation whose placeholders don't fit falls back to the English. Ask for it on the game thread.
/// </summary>
internal readonly struct ChatTr(ILocalizationProvider? provider, PlayerSlot slot)
{
    public string this[ChatText text]
        => provider?.GetText(slot, text.Key) ?? text.English;

    public string Format<T1>(ChatText text, T1 a)
    {
        try
        {
            return ZString.Format(this[text], a);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a);
        }
    }

    public string Format<T1, T2>(ChatText text, T1 a, T2 b)
    {
        try
        {
            return ZString.Format(this[text], a, b);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a, b);
        }
    }

    public string Format<T1, T2, T3>(ChatText text, T1 a, T2 b, T3 c)
    {
        try
        {
            return ZString.Format(this[text], a, b, c);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a, b, c);
        }
    }

    public string Format<T1, T2, T3, T4>(ChatText text, T1 a, T2 b, T3 c, T4 d)
    {
        try
        {
            return ZString.Format(this[text], a, b, c, d);
        }
        catch (FormatException)
        {
            return ZString.Format(text.English, a, b, c, d);
        }
    }

    public string Track(int track)
        => track <= 0 ? this[ChatTexts.TrackMain] : Format(ChatTexts.TrackBonus, track);
}

/// <summary>
///     Every text the Timer prints in chat, by key. The locale file (Plugin/Timer.Localization/locales/surftimer.json)
///     carries each of them; a test keeps the two in step.
/// </summary>
internal static class ChatTexts
{
    // ---- shared
    public static readonly ChatText TrackMain       = new ("chat.track.main", "Main");
    public static readonly ChatText TrackBonus      = new ("chat.track.bonus", "Bonus {0}");
    public static readonly ChatText TrackStage      = new ("chat.track.stage", "Stage {0}");
    public static readonly ChatText TrackBonusStage = new ("chat.track.bonus_stage", "Bonus {0} Stage {1}");
    public static readonly ChatText Checkpoint      = new ("chat.checkpoint", "CP{0}: {1}");
    public static readonly ChatText VsSr            = new ("chat.vs_sr", " | SR {0}");
    public static readonly ChatText VsPb            = new ("chat.vs_pb", " | PB {0}");
    public static readonly ChatText Hours           = new ("chat.duration.hours", "{0}h {1}m");
    public static readonly ChatText Minutes         = new ("chat.duration.minutes", "{0}m");

    // ---- finished runs
    public static readonly ChatText Finish      = new ("chat.finish", "{0} finished {1} - {2} in {3}");
    public static readonly ChatText FinishSr    = new ("chat.finish.sr", "New SR!");
    public static readonly ChatText FinishVsSr  = new ("chat.finish.vs_sr", " (SR {0})");
    public static readonly ChatText FinishVsPb  = new ("chat.finish.vs_pb", " (PB {0})");
    public static readonly ChatText FinishRank  = new ("chat.finish.rank", " #{0}/{1}");
    public static readonly ChatText PracticeRun = new ("chat.record.practice", "Practice run — not saved.");

    public static readonly ChatText SaveQueueFull   = new ("chat.save.queue_full", "Remote submission queue is full; this run was not queued.");
    public static readonly ChatText SaveNotQueued   = new ("chat.save.not_queued", "Remote submission could not be queued; this run was not queued.");
    public static readonly ChatText SaveRejected    = new ("chat.save.rejected", "Remote backend rejected this run; no record result was published.");
    public static readonly ChatText SaveUnconfirmed = new ("chat.save.unconfirmed", "Remote score result was not confirmed; no record result was published.");

    // ---- timer
    public static readonly ChatText MissingStages      = new ("chat.timer.missing_stages", "Missing stages, stopping timer");
    public static readonly ChatText MissingCheckpoints = new ("chat.timer.missing_checkpoints", "Timer stopped: missing checkpoints");

    public static readonly ChatText Unverified = new ("chat.timer.unverified",
                                                      "Your Steam account has not been verified yet. The server may not be connected to Steam. Please wait and try again.");

    // ---- record commands
    public static readonly ChatText UsageSwr  = new ("chat.usage.swr", "Usage: !swr <stage>");
    public static readonly ChatText UsageSpb  = new ("chat.usage.spb", "Usage: !spb <stage>");
    public static readonly ChatText UsageBtop = new ("chat.usage.btop", "Usage: !btop [bonus]");
    public static readonly ChatText UsageBwr  = new ("chat.usage.bwr", "Usage: !bwr [bonus]");
    public static readonly ChatText UsageBpb  = new ("chat.usage.bpb", "Usage: !bpb [bonus]");

    public static readonly ChatText Sr          = new ("chat.sr", "SR: {0}");
    public static readonly ChatText SrNone      = new ("chat.sr.none", "No SR found for this track.");
    public static readonly ChatText SrStage     = new ("chat.sr.stage", "Stage {0} SR: {1} by {2}");
    public static readonly ChatText SrStageNone = new ("chat.sr.stage_none", "No SR found for stage {0}.");
    public static readonly ChatText SrBonus     = new ("chat.sr.bonus", "Bonus {0} SR: {1} by {2}");
    public static readonly ChatText SrBonusNone = new ("chat.sr.bonus_none", "No SR found for bonus {0}.");

    public static readonly ChatText MapNoMatch      = new ("chat.map.no_match", "No map matches \"{0}\".");
    public static readonly ChatText MapMatches      = new ("chat.map.matches", "{0} maps match: {1}");
    public static readonly ChatText MapLookupFailed = new ("chat.map.lookup_failed", "Couldn't look up the maps; try again later.");

    public static readonly ChatText RecordDeleted        = new ("chat.record.deleted", "Deleted {0}'s {1} run.");
    public static readonly ChatText RecordAlreadyDeleted = new ("chat.record.already_deleted", "That run was already deleted.");
    public static readonly ChatText RecordDeleteFailed   = new ("chat.record.delete_failed", "Couldn't delete that run; see the server log.");

    public static readonly ChatText Pb          = new ("chat.pb", "PB: {0} (#{1}/{2})");
    public static readonly ChatText PbNone      = new ("chat.pb.none", "No personal best found for this track.");
    public static readonly ChatText PbStage     = new ("chat.pb.stage", "Stage {0} PB: {1}");
    public static readonly ChatText PbStageNone = new ("chat.pb.stage_none", "No PB found for stage {0}.");
    public static readonly ChatText PbVsSr      = new ("chat.pb.vs_sr", " (SR {0})");
    public static readonly ChatText PbBonus     = new ("chat.pb.bonus", "Bonus {0} PB: {1} (#{2}/{3})");
    public static readonly ChatText PbBonusNone = new ("chat.pb.bonus_none", "No PB found for bonus {0}.");

    public static readonly ChatText Rank     = new ("chat.rank", "Rank: #{0}/{1} | PB: {2}");
    public static readonly ChatText RankNone = new ("chat.rank.none", "No record found. Complete the map first.");

    public static readonly ChatText Top           = new ("chat.top", "#1: {0} ({1} records)");
    public static readonly ChatText TopNone       = new ("chat.top.none", "No records found for this track.");
    public static readonly ChatText TopRow        = new ("chat.top.row", "#{0}: {1} - {2}");
    public static readonly ChatText TopBonus      = new ("chat.top.bonus", "Top records for Bonus {0}:");
    public static readonly ChatText TopBonusNone  = new ("chat.top.bonus_none", "No records found for bonus {0}.");

    public static readonly ChatText CprTitle = new ("chat.cpr.title", "PB vs SR checkpoints:");
    public static readonly ChatText CprFinal = new ("chat.cpr.final", "Final: {0}");
    public static readonly ChatText CprNoSr  = new ("chat.cpr.no_sr", "No SR checkpoints available.");
    public static readonly ChatText CprNoPb  = new ("chat.cpr.no_pb", "No checkpoint data for your PB.");

    public static readonly ChatText RecentTitle    = new ("chat.recent.title", "Recent records:");
    public static readonly ChatText RecentNone     = new ("chat.recent.none", "No recent records.");
    public static readonly ChatText RecentRow      = new ("chat.recent.row", "{0} | {1}");
    public static readonly ChatText RecentRowBonus = new ("chat.recent.row_bonus", "{0} B{1} | {2}");

    // ---- map info
    public static readonly ChatText MapTier        = new ("chat.map.tier", "{0} | Tier: {1}");
    public static readonly ChatText MapPlaytime    = new ("chat.map.playtime", "Playtime on {0}: {1} | Plays: {2}");
    public static readonly ChatText MapInfo        = new ("chat.map.info", "{0} | Tier: {1} | Mode: {2}");
    public static readonly ChatText MapType        = new ("chat.map.type", "Type: {0}");
    public static readonly ChatText MapLinear      = new ("chat.map.linear", "Linear");
    public static readonly ChatText MapStaged      = new ("chat.map.staged", "Staged");
    public static readonly ChatText MapStages      = new ("chat.map.stages", " | Stages: {0}");
    public static readonly ChatText MapCheckpoints = new ("chat.map.checkpoints", " | Checkpoints: {0}");
    public static readonly ChatText MapBonuses     = new ("chat.map.bonuses", " | Bonuses: {0}");
    public static readonly ChatText MapSr          = new ("chat.map.sr", "SR: {0} | Completions: {1}");
    public static readonly ChatText MapNoSr        = new ("chat.map.no_sr", "None");
    public static readonly ChatText MapPlayed      = new ("chat.map.played", "Played: {0} times | Total: {1}");

    // ---- practice
    public static readonly ChatText UsageTele        = new ("chat.usage.tele", "Usage: !tele <n>  (1-based loc index)");
    public static readonly ChatText LocDead          = new ("chat.loc.dead", "You must be alive to use practice commands.");
    public static readonly ChatText LocNoclip        = new ("chat.loc.noclip", "Cannot saveloc while noclipping or spectating.");
    public static readonly ChatText LocSavePaused    = new ("chat.loc.save_paused", "Cannot saveloc while the timer is paused.");
    public static readonly ChatText LocSaved         = new ("chat.loc.saved", "Saved location #{0}.");
    public static readonly ChatText LocNone          = new ("chat.loc.none", "No saved locations.");
    public static readonly ChatText LocBadIndex      = new ("chat.loc.bad_index", "Invalid loc index. Valid: 1..{0}");
    public static readonly ChatText LocTelePaused    = new ("chat.loc.tele_paused", "Cannot teleport while the timer is paused.");
    public static readonly ChatText LocOtherStyle    = new ("chat.loc.other_style", "That location was saved on a different style.");
    public static readonly ChatText LocReplayBusy    = new ("chat.loc.replay_busy", "Saving a stage replay, try again in a moment.");
    public static readonly ChatText LocReplayLost    = new ("chat.loc.replay_lost", "This location's replay is gone, so the run continues as practice.");
    public static readonly ChatText LocTeleported    = new ("chat.loc.teleported", "Teleported to loc #{0}/{1}.");
    public static readonly ChatText LocAtLast        = new ("chat.loc.at_last", "Already at the last loc.");
    public static readonly ChatText LocAtFirst       = new ("chat.loc.at_first", "Already at the first loc.");
    public static readonly ChatText LocCleared       = new ("chat.loc.cleared", "Cleared all saved locations.");
    public static readonly ChatText LocClearConfirm  = new ("chat.loc.clear_confirm", "Clear all {0} saved locations? Clear again within {1} seconds to confirm.");
    public static readonly ChatText LocList          = new ("chat.loc.list", "Saved locations: {0} (current #{1})");
    public static readonly ChatText LocListRow       = new ("chat.loc.list_row", "#{0}: {1}");
    public static readonly ChatText LocListSegmented = new ("chat.loc.list_segmented", " (segmented)");
    public static readonly ChatText LocListCurrent   = new ("chat.loc.list_current", " <- current");

    // ---- hide
    public static readonly ChatText HideOn  = new ("chat.hide.on", "Other players are now hidden.");
    public static readonly ChatText HideOff = new ("chat.hide.off", "Other players are now visible.");

    // ---- spectating
    public static readonly ChatText FindNone        = new ("chat.find.none", "No player named \"{0}\" is on the server.");
    public static readonly ChatText FindMany        = new ("chat.find.many", "More than one player matches \"{0}\".");
    public static readonly ChatText SpecUnavailable = new ("chat.spec.unavailable", "{0} can't be spectated right now.");
    public static readonly ChatText SpecsList       = new ("chat.specs.list", "Spectating {0} ({1}): {2}");
    public static readonly ChatText SpecsNone       = new ("chat.specs.none", "Nobody is spectating {0}.");

    // ---- sounds
    public static readonly ChatText SoundsOn  = new ("chat.sounds.on", "Timer sounds are on.");
    public static readonly ChatText SoundsOff = new ("chat.sounds.off", "Timer sounds are off.");

    // ---- points ranks
    public static readonly ChatText TopPlayersTitle    = new ("chat.ptop.title", "Top players:");
    public static readonly ChatText TopPlayersRow      = new ("chat.ptop.row", "#{0} {1} - {2} points");
    public static readonly ChatText TopPlayersYou      = new ("chat.ptop.you", "You: #{0}/{1}");
    public static readonly ChatText TopPlayersUnranked = new ("chat.ptop.unranked", "You aren't ranked yet: finish a map to earn points.");
    public static readonly ChatText TopPlayersNone     = new ("chat.ptop.none", "No ranked players yet.");

    public static readonly IReadOnlyList<ChatText> All = typeof(ChatTexts).GetFields(BindingFlags.Public | BindingFlags.Static)
                                                                          .Where(f => f.FieldType == typeof(ChatText))
                                                                          .Select(f => (ChatText) f.GetValue(null)!)
                                                                          .ToArray();
}
