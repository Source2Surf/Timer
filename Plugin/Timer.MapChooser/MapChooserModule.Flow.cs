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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using Timer.MapChooser.Logic;

namespace Timer.MapChooser;

public sealed partial class MapChooserModule
{
    // Retried this long after a change that didn't happen (a map that failed to load).
    private const float ChangeRetry = 30f;

    private void Tick()
    {
        if (!_mapRunning)
        {
            return;
        }

        var now = Now;

        if (_vote is { } vote && now >= vote.EndsAt)
        {
            FinishVote(now);
        }

        if (!float.IsNaN(_changeAt))
        {
            if (now >= _changeAt)
            {
                ChangeLevel(now);
            }

            return;
        }

        var left = _clock.TimeLeft(now);

        if (left <= 0)
        {
            TimeUp(now);
        }
        else if (_vote is null && _nextMap is null && left <= _config.VoteBeforeEnd && Players() > 0)
        {
            StartVote(MapVoteKind.EndOfMap, now);
        }
    }

    /// <summary>
    ///     Maps that can come up: not this one, and not one played lately unless that leaves none.
    /// </summary>
    private List<PoolMap> Candidates()
    {
        var others = _pool.Where(x => !x.Name.Equals(_currentMap, StringComparison.OrdinalIgnoreCase)).ToList();
        var fresh  = others.Where(x => !_recent.Contains(x.Name)).ToList();

        return fresh.Count > 0 ? fresh : others;
    }

    private bool StartVote(MapVoteKind kind, float now)
    {
        var extend = kind == MapVoteKind.EndOfMap && _clock.Extends < _config.MaxExtends ? _config.ExtendMinutes : 0;

        var nominated = _nominations.Maps.Select(x => _poolByName.GetValueOrDefault(x)).OfType<PoolMap>();
        var options   = VoteBuilder.Build(nominated, Candidates(), _config.VoteMaps, extend, _random);

        if (options.All(x => x.IsExtend))
        {
            _logger.LogWarning("No maps to vote for: the pool has {count} maps, all recent or excluded", _pool.Count);

            return false;
        }

        _vote = new MapVote(kind, options, now + _config.VoteDuration);
        Array.Clear(_cursor);
        Changed();

        Announce(ChooserTexts.VoteStarted, options.Count);

        foreach (var client in Humans())
        {
            for (var i = 0; i < options.Count; i++)
            {
                Reply(client, ChooserTexts.VoteOption, i + 1, OptionLabel(client, options[i]));
            }
        }

        return true;
    }

    private void FinishVote(float now)
    {
        if (_vote is not { } vote)
        {
            return;
        }

        _vote = null;
        Changed();

        var winner = vote.Winner(_random);

        if (winner < 0)
        {
            return;
        }

        var option = vote.Options[winner];

        if (option.IsExtend)
        {
            _clock.Extend(option.ExtendMinutes);
            Announce(ChooserTexts.VoteExtended, option.ExtendMinutes);

            return;
        }

        _nextMap = option.Map;
        _nominations.Clear();
        Announce(ChooserTexts.VoteWon, Map(option.Map), vote.Counts[winner], vote.Voters);

        if (vote.Kind == MapVoteKind.RockTheVote)
        {
            ScheduleChange(now);
        }
    }

    /// <summary>
    ///     The map is over: a running vote ends now, and the map changes, to a nominated or random one if nothing was
    ///     voted for.
    /// </summary>
    private void TimeUp(float now)
    {
        FinishVote(now);

        if (_clock.TimeLeft(now) > 0)
        {
            return;
        }

        _nextMap ??= _nominations.Maps.FirstOrDefault() ?? RandomCandidate();

        if (_nextMap is null)
        {
            _logger.LogWarning("The map is over but there is no map to change to; extending it");
            _clock.Extend(_config.ExtendMinutes);

            return;
        }

        ScheduleChange(now);
    }

    private string? RandomCandidate()
    {
        var candidates = Candidates();

        return candidates.Count == 0 ? null : candidates[_random.Next(candidates.Count)].Name;
    }

    private void ScheduleChange(float now)
    {
        _changeAt = now + _config.ChangeDelay;
        Changed();
        Announce(ChooserTexts.Changing, Map(_nextMap!), (int) _config.ChangeDelay);
    }

    private void ChangeLevel(float now)
    {
        var map = _nextMap!;
        _changeAt = now + ChangeRetry;

        var entry  = _poolByName.GetValueOrDefault(map);
        var hosted = entry is { Hosted: true }
            ? entry.Name
            : _modSharp.ListWorkshopMaps().Select(x => x.Name).FirstOrDefault(x => x.Equals(map, StringComparison.OrdinalIgnoreCase));

        if (hosted is not null)
        {
            _modSharp.ServerCommand($"ds_workshop_changelevel {hosted}");
        }
        else if (entry is { WorkshopId: not 0 })
        {
            _modSharp.ServerCommand($"host_workshop_map {entry.WorkshopId}");
        }
        else if (_modSharp.IsMapValid(map))
        {
            _modSharp.ChangeLevel(map);
        }
        else
        {
            _logger.LogError("Can't change to {map}: it is neither a workshop map nor installed", map);
        }
    }

    private void CheckRockTheVote(PlayerSlot? leaving = null)
    {
        if (_rtv.Count == 0 || _vote is not null || !float.IsNaN(_changeAt))
        {
            return;
        }

        if (_rtv.Count < RockTheVote.Needed(Players(leaving), _config.RtvRatio))
        {
            return;
        }

        _rtv.Clear();
        Changed();

        var now = Now;

        if (_nextMap is not null)
        {
            ScheduleChange(now);
        }
        else if (!StartVote(MapVoteKind.RockTheVote, now))
        {
            Announce(ChooserTexts.RtvNoMaps);
        }
    }
}
