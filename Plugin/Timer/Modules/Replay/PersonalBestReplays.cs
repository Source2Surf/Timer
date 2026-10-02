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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules;

/// <summary>
///     Each player's PB replay, for the HUD's live difference against their PB.
/// </summary>
internal interface IPersonalBestReplays
{
    /// <summary>
    ///     The replay of <paramref name="pb" /> (the player's best on a track, any style) and its spatial index, once
    ///     loaded; until then null, and it starts loading. A newer PB replaces the one held.
    /// </summary>
    ReplayContent? GetPersonalBest(PlayerSlot slot, RunRecord pb, out ClosestFrameIndex? index);
}

// One PB replay per player, for the run they're on: from the server record's replay when theirs is the record,
// else from disk or the replay store like any run. A failed load is retried now and then, since a new PB's replay
// can still be on its way to disk when its record arrives.
internal partial class ReplayPlaybackModule : IPersonalBestReplays
{
    private const float PersonalBestRetrySeconds = 10f;

    private sealed class PersonalBest
    {
        public required long RunId;
        public ReplayContent?     Content;
        public ClosestFrameIndex? Index;
        public bool               Loading;
        public float              RetryAt;
    }

    private readonly PersonalBest?[] _personalBests = new PersonalBest?[PlayerSlot.MaxPlayerCount];

    public ReplayContent? GetPersonalBest(PlayerSlot slot, RunRecord pb, out ClosestFrameIndex? index)
    {
        // Theirs is the server record: its replay and index are already here.
        if (_replayCache.TryGetValue((pb.Style, pb.Track, 0), out var record)
            && record.Header.SteamId == pb.SteamId
            && MathF.Abs(record.Header.Time - pb.Time) < 0.01f
            && _closestFrameIndices.TryGetValue((pb.Style, pb.Track, 0), out var recordIndex))
        {
            index = recordIndex;

            return record;
        }

        var entry = _personalBests[slot];

        if (entry is null || entry.RunId != pb.Id)
        {
            entry                = new PersonalBest { RunId = pb.Id };
            _personalBests[slot] = entry;
            LoadPersonalBest(slot, entry, pb);
        }
        else if (entry.Content is null && !entry.Loading && _bridge.GlobalVars.CurTime >= entry.RetryAt)
        {
            LoadPersonalBest(slot, entry, pb);
        }

        index = entry.Index;

        return entry.Content;
    }

    private void LoadPersonalBest(PlayerSlot slot, PersonalBest entry, RunRecord pb)
    {
        entry.Loading = true;

        var mapName = _bridge.CurrentMapName;
        var token   = _mapRecordLoadToken.Token;

        Task.Run(async () =>
        {
            ReplayContent?     content = null;
            ClosestFrameIndex? index   = null;

            try
            {
                content = LoadRunFromDisk(mapName, pb) ?? await LoadRunFromRemote(mapName, pb, true).ConfigureAwait(false);

                // Built here: it can take a few ms for a long run, too long for the game thread.
                if (content is { Frames.Count: > 0 })
                {
                    index = new ClosestFrameIndex(content.Frames);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load the PB replay of run {RunId}", pb.Id);
            }

            try
            {
                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                                              {
                                                                  // Replaced by a newer PB, or the player left.
                                                                  if (_personalBests[slot] != entry)
                                                                  {
                                                                      return;
                                                                  }

                                                                  entry.Loading = false;

                                                                  if (index is null)
                                                                  {
                                                                      entry.RetryAt = _bridge.GlobalVars.CurTime + PersonalBestRetrySeconds;

                                                                      return;
                                                                  }

                                                                  entry.Content = content;
                                                                  entry.Index   = index;
                                                              },
                                                              token)
                             .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The map changed while it loaded.
            }
        }, token);
    }

    private void ForgetPersonalBest(PlayerSlot slot)
        => _personalBests[slot] = null;
}
