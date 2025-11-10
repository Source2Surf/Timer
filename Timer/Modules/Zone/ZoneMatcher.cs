/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
 
using System.Linq;
using System.Text.RegularExpressions;

namespace SurfTimer.Modules.Zone;

internal static partial class ZoneMatcher
{
    private static readonly string[] StartZoneName = ["map_start", "s1_start", "stage1_start", "timer_startzone", "zone_start"];
    private static readonly string[] EndZoneName   = ["map_end", "timer_endzone", "zone_end"];

    public static bool IsStartZone(string name)
        => StartZoneName.Contains(name);

    public static bool IsEndZone(string name)
        => EndZoneName.Contains(name);

    private static bool IsZone(Regex regex, string targetName, out int track)
    {
        var match = regex.Match(targetName);

        // Check for a successful match and if our named group 'track' was captured.
        // This is much more efficient and clearer than looping through all groups.
        if (match.Success && match.Groups["track"].Success)
        {
            // We can be confident in int.Parse because the regex ensures it's a valid number,
            // but TryParse is safer against potential regex bugs or future changes.
            return int.TryParse(match.Groups["track"].Value, out track);
        }

        track = -1;

        return false;
    }

    [GeneratedRegex("""
                    ^
                    (?:s|stage)              # Prefix: 's' or 'stage'
                    (?<track>[1-9][0-9]?)   # Capture the track number (1-99)
                    _start                   # Suffix
                    $
                    """,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex StageZoneRegex();

    public static bool IsStageZone(string targetName, out int track)
        => IsZone(StageZoneRegex(), targetName, out track);

    [GeneratedRegex("""
                    ^
                    map_(?:cp|checkpoint)    # Prefix: 'map_cp' or 'map_checkpoint'
                    (?<track>[1-9][0-9]?)   # Capture the track number (1-99)
                    $
                    """,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex CheckpointRegex();

    public static bool IsCheckpointZone(string targetName, out int track)
        => IsZone(CheckpointRegex(), targetName, out track);

    [GeneratedRegex("""
                    ^
                    (?:
                        (?:b|bonus)(?<track>[1-9][0-9]?)_start |         # Case 1: b1_start, bonus1_start
                        timer_bonus(?<track>[1-9][0-9]?)_startzone     # Case 2: timer_bonus1_startzone
                    )
                    $
                    """,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex BonusStartRegex();

    public static bool IsBonusStartZone(string targetName, out int track)
        => IsZone(BonusStartRegex(), targetName, out track);

    [GeneratedRegex("""
                    ^
                    (?:
                        (?:b|bonus)(?<track>[1-9][0-9]?)_end |           # Case 1: b1_end, bonus1_end
                        timer_bonus(?<track>[1-9][0-9]?)_endzone       # Case 2: timer_bonus1_endzone
                    )
                    $
                    """,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex BonusEndRegex();

    public static bool IsBonusEndZone(string targetName, out int track)
        => IsZone(BonusEndRegex(), targetName, out track);

    [GeneratedRegex("""
                    ^
                    b(?:onus)?                    # 'b' or 'bonus'
                    (?<bonus>[1-9]\d*)            # Capture the bonus track number (avoids 0)
                    _                             # Separator
                    c(?:heck)?p(?:oint)?          # 'cp' or 'checkpoint' (and variants)
                    (?<checkpoint>[1-9]\d*)      # Capture the checkpoint number (avoids 0)
                    $
                    """,
                    RegexOptions.IgnorePatternWhitespace | RegexOptions.IgnoreCase)]
    private static partial Regex BonusCheckpointRegex();

    public static bool IsBonusCheckpointZone(string targetName, out int bonusTrack, out int checkpoint)
    {
        bonusTrack = -1;
        checkpoint = -1;

        var match = BonusCheckpointRegex().Match(targetName);

        return match.Success
               && int.TryParse(match.Groups["bonus"].Value,      out bonusTrack)
               && int.TryParse(match.Groups["checkpoint"].Value, out checkpoint);
    }
}
