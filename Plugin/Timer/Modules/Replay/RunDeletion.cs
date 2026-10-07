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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal interface IRunDeletionListener
{
    /// <summary>
    ///     On the game thread, after an admin deleted a run and its board was reloaded: removes the run's replay
    ///     files, and on the current map swaps its board's replay for the new record's.
    /// </summary>
    void OnRunDeleted(string mapName, bool currentMap, RunRecord record, DeletedRun deleted);
}

internal partial class ReplayPlaybackModule : IRunDeletionListener
{
    // Replays of runs deleted on this map, by board: a load already in flight must not bring one back.
    private readonly HashSet<(int style, int track, int stage, ulong steamId, float time)> _deletedReplays = [];

    public void OnRunDeleted(string mapName, bool currentMap, RunRecord record, DeletedRun deleted)
    {
        DeleteReplayFiles(mapName, record, deleted.ReplayUrls);

        if (!currentMap || _hasNoBotParam)
        {
            return;
        }

        var key = (record.Style, record.Track, record.Stage);
        _deletedReplays.Add((record.Style, record.Track, record.Stage, record.SteamId, record.Time));

        if (Central is { RunId: var playing } central && playing == record.Id)
        {
            GoIdle(central);
        }

        if (!_replayCache.TryGetValue(key, out var cached) || !IsDeletedReplay(key, cached.Header))
        {
            return;
        }

        _replayCache.Remove(key);
        _closestFrameIndices.Remove(key);

        foreach (var bot in _replayBots)
        {
            if (bot.Type != EReplayBotType.Looping || !ReferenceEquals(bot.Header, cached.Header))
            {
                continue;
            }

            // On to the next replay in its rotation, or a wildcard when none is left.
            StopBotTimer(bot);
            bot.Header = null;
            bot.Frames = [];

            if (bot.Config.StageBot)
            {
                FindNextStageReplay(bot);
            }
            else
            {
                FindNextReplay(bot);
            }

            if (bot.Header is null)
            {
                bot.Style = -1;
                bot.Track = -1;
            }

            StartReplay(bot);
        }

        LoadBoardReplay(mapName, key);
    }

    private bool IsDeletedReplay((int style, int track, int stage) key, ReplayFileHeader header)
        => _deletedReplays.Contains((key.style, key.track, key.stage, header.SteamId, header.Time));

    // The board's new record's replay, the way the map start loads them.
    private void LoadBoardReplay(string mapName, (int style, int track, int stage) key)
    {
        if (_recordModule.GetWR(key.style, key.track, key.stage) is not { } wr)
        {
            return;
        }

        List<(int style, int track, int stage, RunRecord wr)> keys = [(key.style, key.track, key.stage, wr)];
        var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(_bridge.CancellationToken, _mapRecordLoadToken.Token);

        Task.Run(async () =>
        {
            try
            {
                var results = new Dictionary<(int style, int track, int stage), ReplayContent>();
                LoadReplaysFromDisk(mapName, keys, results, linkedToken.Token);
                await LoadMissingReplaysFromRemote(mapName, keys, results, linkedToken.Token);

                if (!results.TryGetValue(key, out var content))
                {
                    return;
                }

                var index = key.stage == 0 ? new ClosestFrameIndex(content.Frames) : null;

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (linkedToken.IsCancellationRequested
                        || IsDeletedReplay(key, content.Header)
                        || (_replayCache.TryGetValue(key, out var existing) && existing.Header.Time <= content.Header.Time))
                    {
                        return;
                    }

                    _replayCache[key] = content;

                    if (index is not null)
                    {
                        _closestFrameIndices[key] = index;
                    }

                    UpdateReplayBots(key.style, key.track, key.stage);
                }, linkedToken.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load the replay for style {Style} track {Track} stage {Stage} after a run was deleted",
                                 key.style, key.track, key.stage);
            }
            finally
            {
                linkedToken.Dispose();
            }
        }, linkedToken.Token);
    }

    // Both places a run's replay can be kept on disk, and every stored copy the backend pointed at.
    private void DeleteReplayFiles(string mapName, RunRecord record, IReadOnlyList<string> urls)
    {
        string[] paths =
        [
            ReplayShared.BuildReplayPath(_replayDirectory, mapName, record.Style, record.Track, record.Stage, record.Id),
            ReplayShared.BuildRecentRunPath(_replayDirectory, mapName, record.Style, record.Track, record.Stage, record.SteamId, record.Id),
        ];

        Task.Run(async () =>
        {
            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Couldn't delete the replay file {Path} of deleted run {RunId}", path, record.Id);
                }
            }

            foreach (var url in urls)
            {
                try
                {
                    await _replayProviderProxy.DeleteStoredReplayAsync(url);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Couldn't delete the stored replay {Url} of deleted run {RunId}", url, record.Id);
                }
            }
        });
    }
}
