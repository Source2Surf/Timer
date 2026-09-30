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

using System.Threading.Tasks;

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
/// Remote replay data provider interface for fetching and storing replay data from remote sources.
/// </summary>
public interface IReplayProvider
{
    static readonly string Identity = typeof(IReplayProvider).FullName!;

    /// <summary>
    /// If true, the provider accepts uploads for runs that are NOT a new personal best.
    /// When false (default), only PB / WR replays are uploaded.
    /// </summary>
    bool UploadNonPersonalBest => false;

    /// <summary>
    /// Gets replay binary data for the specified map, style, and track.
    /// When steamId is null, returns the world record (WR) replay; otherwise returns the specified player's best replay.
    /// </summary>
    /// <returns>Replay binary data, or null if not found</returns>
    Task<byte[]?> GetReplayAsync(string mapName, int style, int track, ulong? steamId = null);

    /// <summary>
    /// Gets stage replay binary data for the specified map, style, track, and stage.
    /// When steamId is null, returns the world record (WR) replay; otherwise returns the specified player's best replay.
    /// </summary>
    /// <returns>Replay binary data, or null if not found</returns>
    Task<byte[]?> GetStageReplayAsync(string mapName, int style, int track, int stage, ulong? steamId = null);

    /// <summary>
    /// Uploads replay binary data to remote storage.
    /// steamId and runId are provided by the caller to avoid redundant header deserialization.
    /// </summary>
    Task UploadReplayAsync(string mapName, int style, int track, ulong steamId, ulong runId, byte[] replayData);

    /// <summary>
    /// Uploads stage replay binary data to remote storage.
    /// </summary>
    Task UploadStageReplayAsync(string mapName, int style, int track, int stage, ulong steamId, ulong runId, byte[] replayData);
}
