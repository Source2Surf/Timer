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
using System.Threading.Tasks;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Zone;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
///     Outcome of a record-save attempt. The ordinal ORDER is part of the contract:
///     consumers use relational comparisons (e.g. <c>result &gt;= NewPersonalRecord</c>
///     for "is a new best"), so members must stay sorted from worst to best outcome.
/// </summary>
public enum EAttemptResult
{
    NoNewRecord,
    NewPersonalRecord,
    NewServerRecord,
}

public interface IRequestManager
{
    static readonly string Identity = typeof(IRequestManager).FullName!;

    const int DefaultRecordLimit = 5000;

#region MapInfo

    /// <summary>
    /// Binds the map being loaded to its Steam Workshop item (0 = not from the workshop), so its data
    /// follows the item when an update renames the map. Call before the map's first request.
    /// </summary>
    void SetMapWorkshopId(string mapName, ulong workshopId);

    /// <summary>
    /// Gets the map, adding it when it's new.
    /// </summary>
    Task<MapProfile> GetMapInfo(string map);

    /// <summary>
    /// Sets the map's main-track tier and queues its score recalculation under the backend's score policy.
    /// </summary>
    Task<ScoreQueueResult> SetMapTierAsync(string mapName, byte tier);

    /// <summary>
    /// Ranks or unranks the map (an unranked map earns no points) and queues its score recalculation.
    /// </summary>
    Task<ScoreQueueResult> SetMapRankedAsync(string mapName, bool ranked)
        => Task.FromResult(new ScoreQueueResult(false, 0, 0, []));

    /// <summary>
    /// Atomically records one completed map session without writing an old map-profile snapshot
    /// over sessions completed by other game servers.
    /// </summary>
    Task IncrementMapStatsAsync(string mapName, float deltaSeconds);

    /// <summary>
    /// Get all map names.
    /// </summary>
    Task<IReadOnlyList<string>> GetAllMapNamesAsync();

    /// <summary>
    /// Every map with its tiers and workshop item, by name. Unlike <see cref="GetMapInfo"/> it never adds a map.
    /// A provider that doesn't implement it returns none.
    /// </summary>
    Task<IReadOnlyList<MapProfile>> GetMapProfilesAsync()
        => Task.FromResult<IReadOnlyList<MapProfile>>([]);

#endregion

#region Record

    Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName, int limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName, int limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapRecords(string mapName,
                                                 int    style,
                                                 int    track,
                                                 int    limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetMapStageRecords(string mapName,
                                                      int    style,
                                                      int    track,
                                                      int    stage,
                                                      int    limit = DefaultRecordLimit);

    Task<IReadOnlyList<RunRecord>> GetPlayerRecords(SteamID steamId, string mapName);

    Task<IReadOnlyList<RunRecord>> GetPlayerStageRecords(SteamID steamId, string mapName);

    Task RemoveMapRecords(string mapName);

    Task<IReadOnlyList<RunCheckpoint>> GetRecordCheckpoints(long recordId);

    Task<IReadOnlyList<RunRecord>> GetRecentRecords(string mapName, SteamID steamId, int limit = 10);

    /// <summary>
    /// A player's PB history on one leaderboard (stage 0 is the map): the runs that were their PB when set, newest
    /// first. The replay menu lists them under My runs; a provider that doesn't implement it lists none.
    /// </summary>
    Task<IReadOnlyList<RunRecord>> GetPlayerRuns(string mapName, SteamID steamId, int style, int track, int stage, int limit = 10)
        => Task.FromResult<IReadOnlyList<RunRecord>>([]);

    /// <summary>
    /// A player's results across every map, for their profile. A provider that doesn't implement it returns
    /// null, and the profile leaves those stats out.
    /// </summary>
    Task<PlayerSummary?> GetPlayerSummary(SteamID steamId)
        => Task.FromResult<PlayerSummary?>(null);

    /// <summary>
    /// The maps a player has finished on one style and track, as map id to their best time. A provider that
    /// doesn't implement it returns none.
    /// </summary>
    Task<IReadOnlyDictionary<ulong, float>> GetCompletedMapsAsync(SteamID steamId, int style, int track)
        => Task.FromResult<IReadOnlyDictionary<ulong, float>>(new Dictionary<ulong, float>());

    /// <summary>
    /// A player's settings in the plugin's binary format, or null when they're all defaults or the provider
    /// doesn't keep them.
    /// </summary>
    Task<byte[]?> GetPlayerSettings(SteamID steamId)
        => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Replaces a player's settings; empty data resets them to defaults. A provider that doesn't keep them
    /// ignores it.
    /// </summary>
    Task SavePlayerSettings(SteamID steamId, byte[] data)
        => Task.CompletedTask;

#endregion

#region Score

    /// <summary>
    /// Queues score recalculation for a map, or for every map when <paramref name="mapName"/> is null, under the
    /// backend's score policy.
    /// </summary>
    Task<ScoreQueueResult> RecalculateMapScoresAsync(string? mapName);

#endregion

#region Zone

    /// <summary>
    /// Gets all custom zones for the specified map.
    /// </summary>
    Task<IReadOnlyList<ZoneData>> GetZonesAsync(string mapName);

    /// <summary>
    /// Replaces all custom zones for the specified map (transactional: delete then insert).
    /// </summary>
    Task SaveZonesAsync(string mapName, IReadOnlyList<ZoneData> zones);

#endregion

    /// <summary>
    /// Get the player's rank by points (1-based) and total ranked player count.
    /// Returns (0, 0) if the player has no points.
    /// </summary>
    Task<(int rank, int total)> GetPlayerPointsRank(SteamID steamId);

    /// <summary>
    /// Tells the backend each style's score factor (timer-styles.jsonc score_factor), so it needs no copy of them.
    /// Returns false when the backend configures its own. A provider that doesn't implement it ignores it.
    /// </summary>
    Task<bool> RegisterStyleFactors(IReadOnlyDictionary<int, double> factors)
        => Task.FromResult(false);

    /// <summary>
    /// The points leaderboard's top players, best first. A provider that doesn't implement it returns none.
    /// </summary>
    Task<IReadOnlyList<RankedPlayer>> GetTopPlayers(int limit)
        => Task.FromResult<IReadOnlyList<RankedPlayer>>([]);

    /// <summary>
    /// Several players' points ranks and points (unranked players are left out) and how many players are ranked. A
    /// provider that doesn't implement it ranks no one.
    /// </summary>
    Task<(IReadOnlyDictionary<SteamID, (int Rank, uint Points)> Players, int Total)> GetPlayersPointsRank(IReadOnlyList<SteamID> steamIds)
        => Task.FromResult<(IReadOnlyDictionary<SteamID, (int Rank, uint Points)>, int)>((new Dictionary<SteamID, (int Rank, uint Points)>(), 0));

    /// <summary>
    /// Atomically increment a player's per-map play time and play count.
    /// </summary>
    Task UpdatePlayerMapStatsAsync(SteamID steamId, string mapName, float deltaSeconds);

    /// <summary>
    /// Get a player's per-map play time and play count.
    /// Returns (0, 0) if no stats exist.
    /// </summary>
    Task<(float playTime, int playCount)> GetPlayerMapStatsAsync(SteamID steamId, string mapName);
}
