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
using Cysharp.Text;
using Source2Surf.Timer.Shared;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     The HUD's formatting and layout arithmetic, free of game state so it can be tested on its own.
/// </summary>
internal static class HudFormat
{
    private const float SnapCatch   = 1.5f; // a panel's centre within this many percent of the centre line snaps onto it
    private const float SnapRelease = 3f;   // ...and has to move this far away to let go, so it doesn't flicker

    /// <summary>
    ///     <paramref name="a" /> - <paramref name="b" /> in whole milliseconds, taken from what each shows, so a
    ///     difference always agrees with the two times on screen. It stays whole: back in float seconds, 15.797
    ///     would come out as 15.79699993 and show as 15.796.
    /// </summary>
    public static long DiffMillis(float a, float b)
        => Utils.Millis(a) - Utils.Millis(b);

    /// <summary>
    ///     The timer's own format (<see cref="Utils.FormatTime(float, bool)" />) to the millisecond: "5.123",
    ///     "01:05.500", "01:02:03.450". Never negative.
    /// </summary>
    public static string FormatTime(float seconds)
        => Utils.FormatTime(seconds > 0 ? seconds : 0, true);

    /// <summary>
    ///     A difference from <see cref="DiffMillis" />: "+1.500" / "-0.400" / "+1:02.030".
    /// </summary>
    public static string FormatDiff(long ms)
    {
        var sb = ZString.CreateStringBuilder(true);

        try
        {
            sb.Append(ms < 0 ? '-' : '+');
            Utils.AppendMillis(ref sb, Math.Abs(ms));

            return sb.ToString();
        }
        finally
        {
            sb.Dispose();
        }
    }

    /// <summary>
    ///     Time played, as long as it reads: "12 min", "3 h 12 min", then whole hours from 10 h ("96 h", "1,240 h").
    /// </summary>
    public static string Duration(HudTr tr, float seconds)
    {
        var minutes = float.IsFinite(seconds) && seconds > 0 ? (long) Math.Min(seconds / 60d, 1e12) : 0;
        var hours   = minutes / 60;

        return hours < 1    ? tr.Format(HudTexts.Minutes, minutes)
            : hours < 10    ? tr.Format(HudTexts.HoursMinutes, hours, minutes % 60)
                              : tr.Format(HudTexts.Hours, Count(hours));
    }

    /// <summary>
    ///     Whether text has Chinese, Japanese or Korean in it, which the client draws in a fallback font.
    /// </summary>
    public static bool HasCjk(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u2E80' and <= '\u9FFF' or >= '\uAC00' and <= '\uD7AF' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFFEF')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     How many pixels a small note is lifted to line up with its value. CJK text comes from a fallback font with
    ///     taller lines, which centres a CJK note low: 2px after a plain number, 1px after CJK text
    ///     ("2 小时 59 分钟  13 次游玩"). Tuned from in-game screenshots.
    /// </summary>
    public static int NoteLift(string value, string note)
        => !HasCjk(note) ? 0
            : HasCjk(value) ? 1
                              : 2;

    /// <summary>
    ///     A countdown in whole seconds, rounded up: "0:14", "12:34", "75:00". Never negative.
    /// </summary>
    public static string Countdown(float seconds)
        => Countdown(WholeSeconds(seconds));

    public static string Countdown(int seconds)
        => ZString.Format("{0}:{1:00}", seconds / 60, seconds % 60);

    /// <summary>
    ///     Seconds left as a countdown shows them: rounded up, never negative.
    /// </summary>
    public static int WholeSeconds(float seconds)
        => float.IsFinite(seconds) && seconds > 0 ? (int) MathF.Min(MathF.Ceiling(seconds), int.MaxValue / 2f) : 0;

    /// <summary>
    ///     A vote option's share of the votes in tenths (0-10), for its bar's vb-N class.
    /// </summary>
    public static int ShareStep(int count, int total)
        => total <= 0 || count <= 0 ? 0 : Math.Clamp((int) Math.Round(count * 10.0 / total, MidpointRounding.AwayFromZero), 0, 10);

    /// <summary>
    ///     A count with thousands separators: "2,364".
    /// </summary>
    public static string Count(long n)
        => n.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    ///     Out of what there is: "of 212 · 19%" (truncated, so 99.9% isn't shown as all of it).
    /// </summary>
    public static string OfTotal(HudTr tr, int done, int total)
        => total > 0
            ? tr.Format(HudTexts.OfTotal, Count(total), (int) Math.Floor(Math.Clamp(done * 100d / total, 0, 100)))
            : tr.Format(HudTexts.OfTotalNone, Count(total));

    /// <summary>
    ///     "+12 u/s" / "-9 u/s", as in "Start: 277 u/s (PR: -13 u/s)".
    /// </summary>
    public static string FormatSpeedDiff(float difference)
    {
        var n = RoundSpeed(difference);

        return n < 0 ? ZString.Concat('-', -n, " u/s") : ZString.Concat('+', n, " u/s");
    }

    public static string Signed(float value)
    {
        var n = RoundSpeed(value);

        return n < 0 ? ZString.Concat('-', -n) : ZString.Concat('+', n);
    }

    // The tier above each bar the value reaches (bars ascending, one per tier above grey).
    public static SsjTier Tier(float value, float[] bars)
    {
        var tier = 0;

        while (tier < bars.Length && value >= bars[tier])
        {
            tier++;
        }

        return (SsjTier) tier;
    }

    // A fraction as a percentage with one decimal.
    public static string Percent(float fraction)
        => ZString.Concat((float.IsFinite(fraction) ? fraction * 100f : 0f).ToString("F1", CultureInfo.InvariantCulture), '%');

    /// <summary>
    ///     What a replay is, for its title and the replay menu: the style unless it's the default, the bonus or
    ///     stage, then SR or its rank, or Run for a player's own run that isn't on the leaderboard.
    ///     "SR", "Stage 3 #4", "Sideways Bonus 1 SR", "Run".
    /// </summary>
    public static string ReplayTag(HudTr tr, string? style, int track, int stage, int rank)
    {
        var part = track > 0 ? tr.Format(HudTexts.BonusN, track)
            : stage > 0      ? tr.Format(HudTexts.StageN, stage)
                               : null;
        var which = rank <= 0 ? tr[HudTexts.Run]
            : rank == 1       ? tr[HudTexts.Sr]
                                : tr.Format(HudTexts.Rank, rank);

        return style is null
            ? part is null ? which : ZString.Concat(part, ' ', which)
            : part is null ? ZString.Concat(style, ' ', which) : ZString.Concat(style, ' ', part, ' ', which);
    }

    /// <summary>
    ///     "aoba's SR", "tofu's #4", "mizu's run" (rank 0: not on the leaderboard), "your run".
    /// </summary>
    public static string Whose(HudTr tr, string name, int rank, bool you)
        => you          ? tr[HudTexts.YourRun]
            : rank <= 0 ? tr.Format(HudTexts.WhoseRun, name)
            : rank == 1 ? tr.Format(HudTexts.WhoseSr, name)
                          : tr.Format(HudTexts.WhoseRank, name, rank);

    /// <summary>
    ///     One of the player's own runs, told apart by its time: "your 31.200 run".
    /// </summary>
    public static string OwnRun(HudTr tr, float time)
        => tr.Format(HudTexts.YourTimedRun, FormatTime(time));

    /// <summary>
    ///     When a run was set, in words that don't depend on anyone's time zone: "Just now", "5 min ago", "3 h ago",
    ///     "2 days ago", then the date.
    /// </summary>
    public static string Ago(HudTr tr, DateTime then, DateTime now)
    {
        var age = now - then;

        return age.TotalMinutes < 1 ? tr[HudTexts.AgoNow]
            : age.TotalHours < 1    ? tr.Format(HudTexts.AgoMinutes, (int) age.TotalMinutes)
            : age.TotalDays < 1     ? tr.Format(HudTexts.AgoHours, (int) age.TotalHours)
            : age.TotalDays < 2     ? tr[HudTexts.AgoDay]
            : age.TotalDays < 30    ? tr.Format(HudTexts.AgoDays, (int) age.TotalDays)
                                      : then.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     "0.5×", "1×", "2×".
    /// </summary>
    public static string SpeedTag(float speed)
        => ZString.Concat(speed.ToString("0.##", CultureInfo.InvariantCulture), '×');

    /// <summary>
    ///     How far into a replay its bot is: from where the run starts (the pre-run frames before it don't count)
    ///     to where it ends.
    /// </summary>
    public static float ReplayElapsed(int currentFrame, int preFrame, int postFrame)
        => (Math.Clamp(currentFrame, preFrame, Math.Max(preFrame, postFrame)) - preFrame) * TimerConstants.TickInterval;

    /// <summary>
    ///     Progress in steps of 2% (0 to 50), for the progress bar's width classes.
    /// </summary>
    public static int ProgressStep(float elapsed, float total)
        => total > 0 && float.IsFinite(elapsed) ? Math.Clamp((int) MathF.Floor(elapsed / total * 50f), 0, 50) : 0;

    /// <summary>
    ///     Pages of <paramref name="perPage" /> items; an empty list still has its one page.
    /// </summary>
    public static int PageCount(int count, int perPage)
        => Math.Max(1, (count + perPage - 1) / perPage);

    /// <summary>
    ///     The speed colour: 1 (gain) when the shown speed went up since the last refresh, -1 (loss) when it went
    ///     down, 0 when it's unchanged. It follows the number on screen, so it behaves the same at any speed; a fixed
    ///     u/s threshold missed high speeds, where each refresh gains only a little.
    /// </summary>
    public static int SpeedTrend(float previous, float current)
        => Math.Sign(RoundSpeed(current) - RoundSpeed(previous));

    /// <summary>
    ///     A speed as a whole number, with a non-finite reading shown as 0 rather than thrown on.
    /// </summary>
    public static int RoundSpeed(float speed)
        => float.IsFinite(speed) ? (int) Math.Clamp(MathF.Round(speed), -1_000_000f, 1_000_000f) : 0;

    // ------------------------------------------------------------------ positions

    /// <summary>
    ///     Whether a centre offset (percent of the screen) snaps onto the centre line.
    /// </summary>
    public static bool SnapsToCentre(float offset, bool wasSnapped)
        => MathF.Abs(offset) < (wasSnapped ? SnapRelease : SnapCatch);

    /// <summary>
    ///     The offset the player sees: whole percents, a snapped axis exactly on the centre line.
    /// </summary>
    public static int ShownOffset(float offset, bool snapped)
        => snapped || !float.IsFinite(offset) ? 0 : (int) MathF.Round(Math.Clamp(offset, -50f, 50f));

    /// <summary>
    ///     An offset of -50..50 as the two classes that carry it (hud_positions.css): tens on the panel as
    ///     mx-/my-<c>tens</c> ("m4" for -40), units 0-9 on its full-screen wrapper as fx-/fy-<c>units</c>.
    ///     -37 is m4 + 3.
    /// </summary>
    public static (string Tens, string Units) SplitOffset(int offset)
    {
        var tens  = (int) Math.Floor(offset / 10d);
        var units = offset - (10 * tens);

        return (tens < 0 ? ZString.Concat('m', -tens) : tens.ToString(), units.ToString());
    }

    /// <summary>
    ///     Keeps a centre offset on screen: up to <paramref name="limit" /> percent either way.
    /// </summary>
    public static float ClampAbs(float value, float limit)
        => Math.Clamp(value, -limit, limit);

    // ------------------------------------------------------------------ timer lines

    /// <summary>
    ///     Whether the blank line at <paramref name="gapAt" /> shows: only between two lines that do.
    /// </summary>
    public static bool GapShown(IReadOnlyList<bool> shown, int gapAt)
    {
        var before = false;
        var after  = false;

        for (var i = 0; i < shown.Count; i++)
        {
            if (i < gapAt)
            {
                before |= shown[i];
            }
            else if (i > gapAt)
            {
                after |= shown[i];
            }
        }

        return before && after;
    }

    /// <summary>
    ///     Which heading spacers show: a spacer follows its group when that group shows and any later one does.
    ///     <paramref name="groups" /> ends with the lines under the heading, which have no spacer of their own.
    /// </summary>
    public static bool[] SpacersShown(IReadOnlyList<bool> groups)
    {
        var spacers = new bool[Math.Max(0, groups.Count - 1)];

        for (var i = 0; i < spacers.Length; i++)
        {
            if (!groups[i])
            {
                continue;
            }

            for (var j = i + 1; j < groups.Count; j++)
            {
                if (groups[j])
                {
                    spacers[i] = true;

                    break;
                }
            }
        }

        return spacers;
    }
}
