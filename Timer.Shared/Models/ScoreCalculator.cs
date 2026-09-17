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

namespace Source2Surf.Timer.Shared.Models;

/// <summary>
/// Stateless static score calculator. All scoring formulas are centralized here with no database or external service dependencies.
/// </summary>
public static class ScoreCalculator
{
    /// <summary>Default base score pool</summary>
    public const double DefaultBasePot = 1000.0;

    /// <summary>Tier exponent base</summary>
    public const double TierExponentBase = 1.8;

    /// <summary>Completion pool ratio (12%)</summary>
    public const double CompletionPoolRatio = 0.12;

    /// <summary>Rank pool ratio (88%)</summary>
    public const double RankPoolRatio = 0.88;

    /// <summary>Main track scale</summary>
    public const double MainTrackScale = 1.0;

    /// <summary>Bonus track scale</summary>
    public const double BonusTrackScale = 0.3;

    /// <summary>Rank score floor ratio (5%)</summary>
    public const double RankFloor = 0.05;

    /// <summary>Rank decay exponent</summary>
    public const double RankDecayExponent = 1.5;

    /// <summary>
    /// Calculate the track pool.
    /// Formula: basePot × 1.8^(tier - 1) × TrackTypeScale × styleFactor
    /// </summary>
    /// <param name="tier">Track difficulty tier, clamped to 1 if less</param>
    /// <param name="isBonus">Whether this is a bonus track</param>
    /// <param name="basePot">Base score pool; uses default when null or &lt;= 0</param>
    /// <param name="styleFactor">Style score multiplier; uses 1.0 when null or negative.
    /// Zero deliberately disables points for this style.</param>
    /// <returns>Track score pool</returns>
    public static double CalculateTrackPool(int tier, bool isBonus, double? basePot = null, double? styleFactor = null)
    {
        var effectiveBasePot = basePot is > 0 ? basePot.Value : DefaultBasePot;
        var effectiveStyleFactor = styleFactor is >= 0 ? styleFactor.Value : 1.0;
        var effectiveTier = Math.Max(1, tier);
        var scale = isBonus ? BonusTrackScale : MainTrackScale;
        return effectiveBasePot * Math.Pow(TierExponentBase, effectiveTier - 1) * scale * effectiveStyleFactor;
    }

    /// <summary>
    /// Calculate the completion pool.
    /// Formula: TrackPool × 0.12
    /// </summary>
    /// <param name="trackPool">Track score pool</param>
    /// <returns>Completion pool</returns>
    public static double CalculateCompletionPool(double trackPool)
    {
        return trackPool * CompletionPoolRatio;
    }

    /// <summary>
    /// Calculate the reward for a single stage.
    /// Formula: CompletionPool / stageCount
    /// </summary>
    /// <param name="trackPool">Track score pool</param>
    /// <param name="stageCount">Number of stages, clamped to 1 if &lt;= 0</param>
    /// <returns>Per-stage reward</returns>
    public static double CalculateStageReward(double trackPool, int stageCount)
    {
        // Clamp stageCount to a minimum of 1
        var effectiveStageCount = Math.Max(1, stageCount);
        return CalculateCompletionPool(trackPool) / effectiveStageCount;
    }

    /// <summary>
    /// Calculate the rank pool.
    /// Formula: TrackPool × 0.88
    /// </summary>
    /// <param name="trackPool">Track score pool</param>
    /// <returns>Rank pool</returns>
    public static double CalculateRankPool(double trackPool)
    {
        return trackPool * RankPoolRatio;
    }

    /// <summary>
    /// Calculate rank-based points.
    /// Formula: MAX(RankPool × 0.05, RankPool × (1 - (rank-1)/total)^RankDecayExponent)
    /// </summary>
    /// <param name="trackPool">Track score pool</param>
    /// <param name="rank">Player rank (1-based)</param>
    /// <param name="total">Total number of completions</param>
    /// <returns>Rank points; returns 0 for edge cases</returns>
    public static double CalculateRankPoints(double trackPool, int rank, int total)
    {
        // Edge cases: return 0 when total=0, rank>total, or rank<1
        if (total <= 0 || rank > total || rank < 1)
        {
            return 0;
        }

        var rankPool = CalculateRankPool(trackPool);
        var floor = rankPool * RankFloor;

        // Decay points: RankPool × (1 - (rank-1)/total)^exp
        var ratio = 1.0 - (double)(rank - 1) / total;
        var decayPoints = rankPool * Math.Pow(ratio, RankDecayExponent);

        return Math.Max(floor, decayPoints);
    }

    /// <summary>
    /// Calculate a player's total track score for a completed run.
    /// Formula: CompletionPool + RankPoints
    /// Note: only players who have completed the track receive a score.
    /// </summary>
    /// <param name="trackPool">Track score pool</param>
    /// <param name="rank">Player rank (1-based)</param>
    /// <param name="total">Total number of completions</param>
    /// <returns>Player track score</returns>
    public static double CalculatePlayerTrackScore(double trackPool, int rank, int total)
    {
        return CalculateCompletionPool(trackPool) + CalculateRankPoints(trackPool, rank, total);
    }

    /// <summary>
    /// Determine whether a track is a bonus track.
    /// Track 0 is the main track; Track > 0 is a bonus track.
    /// </summary>
    /// <param name="track">Track number</param>
    /// <returns>True if the track is a bonus track</returns>
    public static bool IsBonus(int track)
    {
        return track > 0;
    }
}
