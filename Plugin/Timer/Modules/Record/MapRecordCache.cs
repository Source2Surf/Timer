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
using System.Threading;
using Microsoft.Extensions.Logging;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules.Record;

internal sealed class MapRecordCache
{
    private readonly List<RunRecord>[,] _mapRecords = new List<RunRecord>[TimerConstants.MAX_STYLE, TimerConstants.MAX_TRACK];
    private readonly Dictionary<(int style, int track, int stage), List<RunRecord>> _stageRecords = [];
    private readonly IReadOnlyList<RunCheckpoint>?[,] _wrCheckpoints
        = new IReadOnlyList<RunCheckpoint>?[TimerConstants.MAX_STYLE, TimerConstants.MAX_TRACK];
    private readonly ILogger _logger;
    private long _generation;
    private long _loadSequence;
    private readonly long[,] _trackLoads = new long[TimerConstants.MAX_STYLE, TimerConstants.MAX_TRACK];
    private readonly Dictionary<(int style, int track, int stage), long> _stageLoads = [];

    internal readonly record struct LoadToken(long Generation, long Sequence);

    // Capture before starting asynchronous work. Clearing the map invalidates every
    // outstanding load, including loads for a previous visit to the same map name.
    public LoadToken BeginLoad() => new(Volatile.Read(ref _generation), Interlocked.Increment(ref _loadSequence));

    // Bumped on the game thread whenever a board changes; the lists are reused in place.
    public int Version { get; private set; }

    private (int Style, int Track, int Stage)[] _boards = [];
    private int _boardsVersion = -1;
    public LoadToken BeginLoad(LoadToken origin) => new(origin.Generation, Interlocked.Increment(ref _loadSequence));
    public bool IsCurrent(LoadToken load) => load.Generation == Volatile.Read(ref _generation);

    public MapRecordCache(ILogger logger)
    {
        _logger = logger;

        for (var s = 0; s < TimerConstants.MAX_STYLE; s++)
        {
            for (var t = 0; t < TimerConstants.MAX_TRACK; t++)
            {
                _mapRecords[s, t] = [];
            }
        }
    }

    /// <summary>
    ///     A map's records by board, each sorted: done off the game thread, which only swaps them in.
    /// </summary>
    public (Dictionary<(int Style, int Track), List<RunRecord>> Tracks, Dictionary<(int Style, int Track, int Stage), List<RunRecord>> Stages)
        Group(IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords)
    {
        var tracks = new Dictionary<(int Style, int Track), List<RunRecord>>();
        var stages = new Dictionary<(int Style, int Track, int Stage), List<RunRecord>>();

        foreach (var record in records)
        {
            if (!tracks.TryGetValue((record.Style, record.Track), out var list))
            {
                tracks[(record.Style, record.Track)] = list = [];
            }

            list.Add(record);
        }

        foreach (var record in stageRecords)
        {
            if (!IsValidStageIndex(record.Stage))
            {
                _logger.LogWarning("Ignore invalid stage record during map cache warm-up. style={style}, track={track}, stage={stage}",
                                   record.Style,
                                   record.Track,
                                   record.Stage);

                continue;
            }

            if (!stages.TryGetValue((record.Style, record.Track, record.Stage), out var list))
            {
                stages[(record.Style, record.Track, record.Stage)] = list = [];
            }

            list.Add(record);
        }

        foreach (var list in tracks.Values)
        {
            list.Sort();
        }

        foreach (var list in stages.Values)
        {
            list.Sort();
        }

        return (tracks, stages);
    }

    public void Populate(IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords, LoadToken load)
        => Populate(Group(records, stageRecords), load);

    public void Populate((Dictionary<(int Style, int Track), List<RunRecord>> Tracks, Dictionary<(int Style, int Track, int Stage), List<RunRecord>> Stages) boards,
                         LoadToken                                                                                                                       load)
    {
        if (!IsCurrent(load)) return;

        foreach (var ((style, track), records) in boards.Tracks)
        {
            RefreshTrack(style, track, records, load, sorted: true);
        }

        foreach (var ((style, track, stage), records) in boards.Stages)
        {
            RefreshStage(style, track, stage, records, load, sorted: true);
        }
    }

    public void RefreshTrack(int style, int track, IReadOnlyList<RunRecord> records, LoadToken load, bool sorted = false)
    {
        if (!IsCurrent(load) || load.Sequence < _trackLoads[style, track]) return;
        _trackLoads[style, track] = load.Sequence;
        var list = _mapRecords[style, track];
        list.Clear();
        list.AddRange(records);

        if (!sorted)
        {
            list.Sort();
        }

        Version++;
    }

    public void RefreshStage(int style, int track, int stage, IReadOnlyList<RunRecord> records, LoadToken load, bool sorted = false)
    {
        var key = (style, track, stage);
        if (!IsCurrent(load) || (_stageLoads.TryGetValue(key, out var sequence) && load.Sequence < sequence)) return;
        _stageLoads[key] = load.Sequence;

        if (!_stageRecords.TryGetValue(key, out var list))
        {
            _stageRecords[key] = list = new List<RunRecord>(records.Count);
        }

        list.Clear();
        list.AddRange(records);

        if (!sorted)
        {
            list.Sort();
        }

        Version++;
    }

    /// <summary>
    ///     A player's new best in place of their old one, where the board's order puts it. A load begun before it
    ///     can't overwrite it; the returned one is for the server record's checkpoints.
    /// </summary>
    public LoadToken Upsert(int style, int track, int stage, RunRecord record)
    {
        var load = new LoadToken(Volatile.Read(ref _generation), Interlocked.Increment(ref _loadSequence));
        List<RunRecord> list;

        if (stage == 0)
        {
            list                      = _mapRecords[style, track];
            _trackLoads[style, track] = load.Sequence;
        }
        else
        {
            if (!IsValidStageIndex(stage))
            {
                return load;
            }

            if (!_stageRecords.TryGetValue((style, track, stage), out list!))
            {
                _stageRecords[(style, track, stage)] = list = [];
            }

            _stageLoads[(style, track, stage)] = load.Sequence;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].SteamId == record.SteamId)
            {
                list.RemoveAt(i);

                break;
            }
        }

        var at = list.BinarySearch(record);
        list.Insert(at < 0 ? ~at : at, record);
        Version++;

        return load;
    }

    public int GetRankForTime(int style, int track, float time)
    {
        var records = _mapRecords[style, track];

        var low  = 0;
        var high = records.Count;

        while (low < high)
        {
            var mid = (int)(((uint) low + (uint) high) >> 1);

            if (records[mid].Time < time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low + 1;
    }

    public RunRecord? GetWR(int style, int track, int stage = 0)
    {
        if (stage == 0)
        {
            var records = _mapRecords[style, track];

            return records.Count > 0 ? records[0] : null;
        }

        if (!IsValidStageIndex(stage))
        {
            throw
                new IndexOutOfRangeException($"Stage index is out of range [1, {TimerConstants.MAX_STAGE}), current: {stage}");
        }

        if (_stageRecords.TryGetValue((style, track, stage), out var stageRecords) && stageRecords.Count > 0)
        {
            return stageRecords[0];
        }

        return null;
    }

    public float? GetWRTime(int style, int track)
    {
        var rec = _mapRecords[style, track];

        if (rec.Count > 0)
        {
            return rec[0].Time;
        }

        return null;
    }

    // The boards with records, by style, track then stage.
    public IReadOnlyList<(int Style, int Track, int Stage)> GetBoards()
    {
        if (_boardsVersion == Version)
        {
            return _boards;
        }

        List<(int Style, int Track, int Stage)> boards = [];

        for (var style = 0; style < TimerConstants.MAX_STYLE; style++)
        {
            for (var track = 0; track < TimerConstants.MAX_TRACK; track++)
            {
                if (_mapRecords[style, track].Count > 0)
                {
                    boards.Add((style, track, 0));
                }
            }
        }

        boards.AddRange(_stageRecords.Where(x => x.Value.Count > 0).Select(x => x.Key));
        boards.Sort();

        _boards        = [.. boards];
        _boardsVersion = Version;

        return _boards;
    }

    public IReadOnlyList<RunRecord> GetRecords(int style, int track)
    {
        return _mapRecords[style, track];
    }

    public IReadOnlyList<RunRecord>? GetStageRecords(int style, int track, int stage) =>
        _stageRecords.GetValueOrDefault((style, track, stage));

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        Version++;
        _stageRecords.Clear();
        _stageLoads.Clear();

        for (var style = 0; style < TimerConstants.MAX_STYLE; style++)
        {
            for (var track = 0; track < TimerConstants.MAX_TRACK; track++)
            {
                _mapRecords[style, track].Clear();
                _trackLoads[style, track] = 0;
                _wrCheckpoints[style, track] = null;
            }
        }
    }

    public void SetWRCheckpoints(int style, int track, IReadOnlyList<RunCheckpoint> checkpoints, LoadToken load)
    {
        if (!IsCurrent(load) || load.Sequence != _trackLoads[style, track]) return;
        _wrCheckpoints[style, track] = checkpoints;
    }

    public IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track)
    {
        return _wrCheckpoints[style, track];
    }

    private static bool IsValidStageIndex(int stage) =>
        stage is >= 1 and < TimerConstants.MAX_STAGE;
}
