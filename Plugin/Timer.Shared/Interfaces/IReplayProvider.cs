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

namespace Source2Surf.Timer.Shared.Interfaces;

/// <summary>
/// Remote replay data provider interface for fetching and storing replay data from remote sources.
/// </summary>
public interface IReplayProvider
{
    static readonly string Identity = typeof(IReplayProvider).FullName!;

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
    /// Gets the replay of one run by its id: a PB, or a slower run the server chose to upload. The replay menu uses it
    /// for a player's own past runs.
    /// </summary>
    /// <returns>Replay binary data, or null if not found; a provider that doesn't implement it finds none</returns>
    Task<byte[]?> GetRunReplayAsync(ulong runId) => Task.FromResult<byte[]?>(null);

    /// <summary>
    /// Which of these runs have a replay in remote storage. The server deletes its own copy of those first when its
    /// replay cache is full, and downloads them again when needed.
    /// </summary>
    /// <returns>The stored run ids; a provider that doesn't implement it confirms none, so nothing is deleted</returns>
    Task<IReadOnlyCollection<ulong>> GetStoredRunIdsAsync(IReadOnlyList<ulong> runIds)
        => Task.FromResult<IReadOnlyCollection<ulong>>([]);

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
