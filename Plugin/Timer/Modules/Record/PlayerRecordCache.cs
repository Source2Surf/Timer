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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Modules.Record;

internal sealed class PlayerRecordCache
{
    private readonly Dictionary<int, Dictionary<(int style, int track), RunRecord>> _records = [];
    private readonly Dictionary<int, Dictionary<(int style, int track, int stage), RunRecord>> _stageRecords = [];
    private readonly ILogger _logger;

    public PlayerRecordCache(ILogger logger)
    {
        _logger = logger;
    }

    public void Populate(PlayerSlot slot, IReadOnlyList<RunRecord> records, IReadOnlyList<RunRecord> stageRecords)
    {
        foreach (var record in records)
        {
            SetRecord(slot, record.Style, record.Track, record);
        }

        foreach (var record in stageRecords)
        {
            var style = record.Style;
            var track = record.Track;
            var stage = record.Stage;

            if (!IsValidStageIndex(stage))
            {
                _logger.LogWarning("Ignore invalid player stage record while loading cache. slot={slot}, style={style}, track={track}, stage={stage}",
                                   slot,
                                   style,
                                   track,
                                   stage);

                continue;
            }

            SetStageRecord(slot, style, track, stage, record);
        }
    }

    public RunRecord? GetRecord(PlayerSlot slot, int style, int track, int stage = 0)
    {
        if (stage == 0)
        {
            return _records.TryGetValue(slot, out var slotRecords)
                && slotRecords.TryGetValue((style, track), out var rec)
                    ? rec
                    : null;
        }

        if (!IsValidStageIndex(stage))
        {
            throw
                new IndexOutOfRangeException($"Stage index is out of range [1, {TimerConstants.MAX_STAGE}), current: {stage}");
        }

        return _stageRecords.TryGetValue(slot, out var slotStageRecords)
            && slotStageRecords.TryGetValue((style, track, stage), out var stageRec)
                ? stageRec
                : null;
    }

    public void SetRecord(PlayerSlot slot, int style, int track, RunRecord record)
    {
        var records = GetOrAddSlotRecords(slot);
        var key = (style, track);
        if (!records.TryGetValue(key, out var best) || record.CompareTo(best) < 0)
        {
            records[key] = record;
        }
    }

    public void SetStageRecord(PlayerSlot slot, int style, int track, int stage, RunRecord record)
    {
        var records = GetOrAddSlotStageRecords(slot);
        var key = (style, track, stage);
        if (!records.TryGetValue(key, out var best) || record.CompareTo(best) < 0)
        {
            records[key] = record;
        }
    }

    // After an admin deleted the player's best: their next-fastest run, or none.
    public void ReplaceRecord(PlayerSlot slot, int style, int track, int stage, RunRecord? record)
    {
        if (stage == 0)
        {
            var records = GetOrAddSlotRecords(slot);
            if (record is null) records.Remove((style, track));
            else records[(style, track)] = record;

            return;
        }

        if (!IsValidStageIndex(stage))
        {
            return;
        }

        var stageRecords = GetOrAddSlotStageRecords(slot);
        if (record is null) stageRecords.Remove((style, track, stage));
        else stageRecords[(style, track, stage)] = record;
    }

    public void Clear(PlayerSlot slot)
    {
        _records.Remove(slot);
        _stageRecords.Remove(slot);
    }

    public void ClearAll()
    {
        _records.Clear();
        _stageRecords.Clear();
    }

    private Dictionary<(int style, int track), RunRecord> GetOrAddSlotRecords(int slot)
    {
        if (!_records.TryGetValue(slot, out var dict))
        {
            dict = [];
            _records[slot] = dict;
        }

        return dict;
    }

    private Dictionary<(int style, int track, int stage), RunRecord> GetOrAddSlotStageRecords(int slot)
    {
        if (!_stageRecords.TryGetValue(slot, out var dict))
        {
            dict = [];
            _stageRecords[slot] = dict;
        }

        return dict;
    }

    private static bool IsValidStageIndex(int stage) =>
        stage >= 1 && stage < TimerConstants.MAX_STAGE;
}
