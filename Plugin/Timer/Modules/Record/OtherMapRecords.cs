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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules.Record;

/// <summary>
///     Other maps' leaderboards for the leaderboard panel: the few viewed last, loaded on first read and refreshed in
///     the background once a minute old. Game thread only.
/// </summary>
internal sealed class OtherMapRecords
{
    internal const int  MaxMaps = 8;
    internal const long StaleMs = 60_000;
    internal const long RetryMs = 10_000;

    private sealed class Entry
    {
        public Dictionary<(int style, int track, int stage), List<RunRecord>>? Boards;
        public (int Style, int Track, int Stage)[] Keys = [];
        public bool Loading;
        public int  Generation;
        public long LoadedAt;
        public long RetryAt;
        public long UsedAt;
    }

    private readonly Dictionary<string, Entry> _maps = new(StringComparer.OrdinalIgnoreCase);

    private readonly Func<string, Task<(IReadOnlyList<RunRecord> Main, IReadOnlyList<RunRecord> Stages)>> _fetch;
    private readonly Func<Action, Task> _onGameThread;
    private readonly Func<long>         _clock;
    private readonly Action<Task>       _track;
    private readonly ILogger            _logger;

    public OtherMapRecords(Func<string, Task<(IReadOnlyList<RunRecord> Main, IReadOnlyList<RunRecord> Stages)>> fetch,
                           Func<Action, Task>                                                                 onGameThread,
                           Func<long>                                                                         clock,
                           Action<Task>                                                                       track,
                           ILogger                                                                            logger)
    {
        _fetch        = fetch;
        _onGameThread = onGameThread;
        _clock        = clock;
        _track        = track;
        _logger       = logger;
    }

    public int Version { get; private set; }

    /// <summary>A board, fastest first; null while the map loads.</summary>
    public IReadOnlyList<RunRecord>? GetRecords(string map, int style, int track, int stage)
        => Touch(map).Boards is { } boards
               ? boards.TryGetValue((style, track, stage), out var records) ? records : []
               : null;

    /// <summary>The boards with records, by style, track then stage; null while the map loads.</summary>
    public IReadOnlyList<(int Style, int Track, int Stage)>? GetBoards(string map)
        => Touch(map) is { Boards: not null } entry ? entry.Keys : null;

    /// <summary>Drops a deleted run now, and reloads the map for the player's next run.</summary>
    public void OnRunDeleted(string map, long runId)
    {
        if (!_maps.TryGetValue(map, out var entry))
        {
            return;
        }

        if (entry.Boards is { } boards)
        {
            foreach (var records in boards.Values)
            {
                records.RemoveAll(x => x.Id == runId);
            }

            Version++;
        }

        // A load already on its way may still hold the run.
        entry.Generation++;
        Load(map, entry);
    }

    private Entry Touch(string map)
    {
        var now = _clock();

        if (!_maps.TryGetValue(map, out var entry))
        {
            if (_maps.Count >= MaxMaps)
            {
                _maps.Remove(_maps.MinBy(x => x.Value.UsedAt).Key);
            }

            entry      = new Entry();
            _maps[map] = entry;
        }

        entry.UsedAt = now;

        if (!entry.Loading && now >= entry.RetryAt && (entry.Boards is null || now - entry.LoadedAt >= StaleMs))
        {
            Load(map, entry);
        }

        return entry;
    }

    private void Load(string map, Entry entry)
    {
        entry.Loading = true;
        _track(LoadAsync(map, entry, entry.Generation));
    }

    private async Task LoadAsync(string map, Entry entry, int generation)
    {
        Dictionary<(int style, int track, int stage), List<RunRecord>>? boards = null;

        try
        {
            var (main, stages) = await _fetch(map).ConfigureAwait(false);

            boards = main.GroupBy(x => (x.Style, x.Track, 0))
                         .Concat(stages.GroupBy(x => (x.Style, x.Track, x.Stage)))
                         .ToDictionary(x => x.Key, x =>
                         {
                             var records = x.ToList();
                             records.Sort();

                             return records;
                         });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the leaderboards of {Map}", map);
        }

        try
        {
            await _onGameThread(() =>
            {
                if (!_maps.TryGetValue(map, out var current) || current != entry || entry.Generation != generation)
                {
                    return;
                }

                entry.Loading = false;

                if (boards is null)
                {
                    entry.RetryAt = _clock() + RetryMs;

                    return;
                }

                entry.Boards   = boards;
                entry.Keys     = [.. boards.Keys.Order()];
                entry.LoadedAt = _clock();
                Version++;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
