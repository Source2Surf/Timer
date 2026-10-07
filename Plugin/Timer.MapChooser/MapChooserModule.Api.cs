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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;

namespace Timer.MapChooser;

public sealed partial class MapChooserModule
{
    private sealed class MenuState
    {
        public string?                         Search;
        public int                             Tier;
        public bool                            UnfinishedOnly;
        public IReadOnlyDictionary<ulong, float>? Completed; // map id to the player's best time, once read
    }

    private readonly MenuState?[] _menus = new MenuState?[PlayerSlot.MaxPlayerCount];

    public int Version => _version;

    public int VersionFor(PlayerSlot slot)
        => _version + _playerVersions[(int) slot];

    public IMapVote? Vote => _vote;

    public int GetVoteChoice(PlayerSlot slot)
        => _vote?.Choice(slot) ?? -1;

    public int GetVoteCursor(PlayerSlot slot)
        => _cursor[(int) slot];

    public void CastVote(PlayerSlot slot, int option)
    {
        if (_clients.GetGameClient(slot) is { } client)
        {
            CastVoteFor(client, option);
        }
    }

    public MapVoteKeys VoteKeys => new (_config.KeyUp, _config.KeyDown, _config.SelectCommand);

    public string? NextMap => _nextMap;

    public float TimeLeft => _mapRunning ? MathF.Max(0, _clock.TimeLeft(Now)) : 0;

    public NominateMenu? GetNominateMenu(PlayerSlot slot)
    {
        if (_menus[(int) slot] is not { } menu)
        {
            return null;
        }

        var mine    = _nominations.Of(slot);
        var entries = new List<NominateEntry>();

        foreach (var map in _pool)
        {
            if (!string.IsNullOrEmpty(menu.Search) && !map.Name.Contains(menu.Search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (menu.Tier > 0 && map.Tier != menu.Tier)
            {
                continue;
            }

            float? best = null;

            if (map.MapId != 0 && menu.Completed is { } completed && completed.TryGetValue(map.MapId, out var time))
            {
                best = time;
            }

            if (menu.UnfinishedOnly && best is not null)
            {
                continue;
            }

            entries.Add(new NominateEntry(map.Name, map.Tier, best, StateOf(map.Name, mine)));
        }

        return new NominateMenu(menu.Search, menu.Tier, menu.UnfinishedOnly, entries);
    }

    public void SetNominateFilter(PlayerSlot slot, int tier, bool unfinishedOnly)
    {
        if (_menus[(int) slot] is not { } menu)
        {
            return;
        }

        menu.Tier           = Math.Clamp(tier, 0, 8);
        menu.UnfinishedOnly = unfinishedOnly;
        Changed((int) slot);
    }

    public void CloseNominateMenu(PlayerSlot slot)
    {
        _menus[(int) slot] = null;
        Changed((int) slot);
    }

    public NominateResult Nominate(PlayerSlot slot, string map)
        => NominateCore(slot, map);

    private NominateState StateOf(string map, string? mine)
    {
        if (map.Equals(_currentMap, StringComparison.OrdinalIgnoreCase))
        {
            return NominateState.Current;
        }

        if (_recent.Contains(map))
        {
            return NominateState.Recent;
        }

        if (map.Equals(mine, StringComparison.OrdinalIgnoreCase))
        {
            return NominateState.Mine;
        }

        return _nominations.Contains(map) ? NominateState.Nominated : NominateState.Available;
    }

    private NominateResult NominateCore(PlayerSlot slot, string map)
    {
        if (!_mapRunning || _vote is not null || _nextMap is not null || !float.IsNaN(_changeAt))
        {
            return NominateResult.Closed;
        }

        if (_poolByName.GetValueOrDefault(map) is not { } entry)
        {
            return NominateResult.NotFound;
        }

        if (entry.Name.Equals(_currentMap, StringComparison.OrdinalIgnoreCase))
        {
            return NominateResult.CurrentMap;
        }

        if (_recent.Contains(entry.Name))
        {
            return NominateResult.Recent;
        }

        var result = _nominations.Add(slot, entry.Name);

        if (result is NominateResult.Nominated or NominateResult.Replaced)
        {
            _menus[(int) slot] = null;
            Changed();

            var name = _clients.GetGameClient(slot)?.Name ?? "?";

            if (result == NominateResult.Nominated)
            {
                Announce(ChooserTexts.NominateDone, name, entry.Name);
            }
            else
            {
                Announce(ChooserTexts.NominateChanged, name, entry.Name);
            }
        }

        return result;
    }

    /// <summary>
    ///     Opens the nominate menu for a HUD to show, reading which maps the player has finished for it.
    /// </summary>
    private void OpenNominateMenu(IGameClient client, string? search, int tier)
    {
        var slot = (int) client.Slot;
        var menu = new MenuState { Search = search, Tier = tier };

        _menus[slot] = menu;
        Changed(slot);

        if (_requests?.Instance is not { } requests)
        {
            return;
        }

        var steamId = client.SteamId;

        _ = Task.Run(async () =>
        {
            try
            {
                var completed = await requests.GetCompletedMapsAsync(steamId, 0, 0);

                _modSharp.InvokeFrameAction(() =>
                {
                    if (_menus[slot] == menu)
                    {
                        menu.Completed = completed;
                        Changed(slot);
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to read the maps {steamId} has finished", steamId);
            }
        });
    }
}
