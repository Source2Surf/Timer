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

    public void Populate(IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords, LoadToken load)
    {
        if (!IsCurrent(load)) return;

        foreach (var group in records.GroupBy(record => (record.Style, record.Track)))
        {
            RefreshTrack(group.Key.Style, group.Key.Track, group.ToList(), load);
        }

        foreach (var group in stageRecords.GroupBy(record => (record.Style, record.Track, record.Stage)))
        {
            var (style, track, stage) = group.Key;

            if (!IsValidStageIndex(stage))
            {
                _logger.LogWarning("Ignore invalid stage record during map cache warm-up. style={style}, track={track}, stage={stage}",
                                   style,
                                   track,
                                   stage);

                continue;
            }

            RefreshStage(style, track, stage, group.ToList(), load);
        }
    }

    public void RefreshTrack(int style, int track, IReadOnlyList<RunRecord> records, LoadToken load)
    {
        if (!IsCurrent(load) || load.Sequence < _trackLoads[style, track]) return;
        _trackLoads[style, track] = load.Sequence;
        var list = _mapRecords[style, track];
        list.Clear();
        list.AddRange(records);
        list.Sort();
    }

    public void RefreshStage(int style, int track, int stage, IReadOnlyList<RunRecord> records, LoadToken load)
    {
        var key = (style, track, stage);
        if (!IsCurrent(load) || (_stageLoads.TryGetValue(key, out var sequence) && load.Sequence < sequence)) return;
        _stageLoads[key] = load.Sequence;

        if (_stageRecords.TryGetValue(key, out var existing))
        {
            existing.Clear();
            existing.AddRange(records);
            existing.Sort();
        }
        else
        {
            var list = new List<RunRecord>(records);
            list.Sort();
            _stageRecords[key] = list;
        }
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

    public IReadOnlyList<RunRecord> GetRecords(int style, int track)
    {
        return _mapRecords[style, track];
    }

    public IReadOnlyList<RunRecord>? GetStageRecords(int style, int track, int stage) =>
        _stageRecords.GetValueOrDefault((style, track, stage));

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
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
