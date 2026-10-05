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
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared.Models;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class RecordModule
{
    private const long MapNamesMs = 300_000;

    private IReadOnlyList<string>? _mapNames;
    private long                   _mapNamesAt;

    public event Action<PlayerSlot, string?>? LeaderboardRequested;

    public int RecordsVersion => _mapCache.Version + _otherMaps.Version;

    public IReadOnlyList<RunRecord>? GetRecords(string map, int style, int track, int stage)
        => IsCurrentMap(map) ? GetRecords(style, track, stage) : _otherMaps.GetRecords(map, style, track, stage);

    public IReadOnlyList<(int Style, int Track, int Stage)>? GetBoards(string map)
        => IsCurrentMap(map) ? _mapCache.GetBoards() : _otherMaps.GetBoards(map);

    private bool IsCurrentMap(string? map)
        => map is null || map.Equals(_bridge.CurrentMapName, StringComparison.OrdinalIgnoreCase);

    // !wr/!sr <map>: an exact name, else the only one containing it, like !nominate.
    private void OpenLeaderboard(PlayerSlot slot, string name)
    {
        if (IsCurrentMap(name))
        {
            LeaderboardRequested?.Invoke(slot, null);

            return;
        }

        if (_mapNames is { } names && Environment.TickCount64 - _mapNamesAt < MapNamesMs)
        {
            OpenLeaderboard(slot, name, names);

            return;
        }

        if (_bridge.ClientManager.GetGameClient(slot) is not { } client)
        {
            return;
        }

        var steamId = client.SteamId;

        _taskTracker.Track(Task.Run(async () =>
        {
            IReadOnlyList<string>? fetched = null;

            try
            {
                fetched = await _request.GetAllMapNamesAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load the map names to find \"{Name}\"", name);
            }

            try
            {
                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (fetched is not null)
                    {
                        _mapNames   = fetched;
                        _mapNamesAt = Environment.TickCount64;
                    }

                    if (_bridge.ClientManager.GetGameClient(steamId) is not { } current)
                    {
                        return;
                    }

                    if (fetched is null)
                    {
                        current.GetPlayerController()?.PrintToChat(_localization.For(current.Slot)[ChatTexts.MapLookupFailed]);

                        return;
                    }

                    OpenLeaderboard(current.Slot, name, fetched);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, _bridge.CancellationToken));
    }

    private void OpenLeaderboard(PlayerSlot slot, string name, IReadOnlyList<string> names)
    {
        var exact   = names.FirstOrDefault(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
        var matches = exact is null ? names.Where(x => x.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList() : [exact];

        if (matches.Count == 1)
        {
            LeaderboardRequested?.Invoke(slot, IsCurrentMap(matches[0]) ? null : matches[0]);

            return;
        }

        if (!_bridge.TryGetController(slot, out var controller))
        {
            return;
        }

        var tr = _localization.For(slot);

        controller.PrintToChat(matches.Count == 0
                                   ? tr.Format(ChatTexts.MapNoMatch, name)
                                   : tr.Format(ChatTexts.MapMatches, matches.Count,
                                               string.Join(", ", matches.Take(6)) + (matches.Count > 6 ? ", ..." : string.Empty)));
    }
}
