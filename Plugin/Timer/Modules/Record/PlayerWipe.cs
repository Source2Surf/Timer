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
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Utilities;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

// !wipeplayer: every run of a player on every map, for a cheater. The first one counts them, the same again within
// half a minute deletes them.
internal partial class RecordModule
{
    private const long WipeConfirmMilliseconds = 30_000;

    private readonly Dictionary<SteamID, (ulong Target, long Until)> _pendingWipes  = [];
    private readonly HashSet<ulong>                                  _wipingPlayers = [];

    private ECommandAction OnCommandWipePlayer(PlayerSlot slot, StringCommand command)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return ECommandAction.Handled;
        }

        var tr  = _localization.For(slot);
        var arg = command.ArgString.Trim();

        if (arg.Length == 0)
        {
            controller.PrintToChat(tr[ChatTexts.WipeUsage]);

            return ECommandAction.Handled;
        }

        if (FindWipeTarget(arg, out var steamId, out var name) is { } error)
        {
            controller.PrintToChat(tr.Format(error, arg));

            return ECommandAction.Handled;
        }

        if (!_wipingPlayers.Add(steamId))
        {
            return ECommandAction.Handled;
        }

        var admin     = client.SteamId;
        var confirmed = _pendingWipes.Remove(admin, out var pending)
                        && pending.Target == steamId
                        && Environment.TickCount64 <= pending.Until;
        var mapName = _bridge.CurrentMapName;
        var load    = _mapCache.BeginLoad();

        _taskTracker.Track(Task.Run(async () =>
        {
            WipedPlayer? wiped = null;

            try
            {
                wiped = await _recordAdministration.WipePlayerAsync(steamId, !confirmed).ConfigureAwait(false);

                if (confirmed)
                {
                    foreach (var (style, track, stage) in wiped.Deleted
                                                               .Where(x => x.Run.WasBest
                                                                           && x.MapName.Equals(mapName, StringComparison.OrdinalIgnoreCase))
                                                               .Select(x => (x.Run.Style, x.Run.Track, x.Run.Stage))
                                                               .Distinct())
                    {
                        await _saver.RefreshBoardAsync(mapName, style, track, stage, load).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to wipe the runs of {SteamId}", steamId);
            }

            try
            {
                await _bridge.ModSharp.InvokeFrameActionAsync(() => OnPlayerWiped(admin, steamId, name, mapName, load, confirmed, wiped))
                             .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, _bridge.CancellationToken));

        return ECommandAction.Handled;
    }

    private void OnPlayerWiped(SteamID admin, ulong steamId, string name, string mapName, MapRecordCache.LoadToken load,
                               bool confirmed, WipedPlayer? wiped)
    {
        _wipingPlayers.Remove(steamId);

        if (confirmed && wiped is { Runs: > 0 })
        {
            _logger.LogInformation("{Admin} wiped {Runs} runs of {Player} ({SteamId}) on {Maps} maps",
                                   admin, wiped.Runs, name, steamId, wiped.Maps);

            var currentMap = _mapCache.IsCurrent(load);
            var player     = _bridge.ClientManager.GetGameClient(new SteamID(steamId));

            foreach (var (map, time, run) in wiped.Deleted)
            {
                var onCurrent = currentMap && map.Equals(mapName, StringComparison.OrdinalIgnoreCase);
                var record = new RunRecord
                {
                    Id = (long) run.RunId, SteamId = run.SteamId, PlayerName = name, Style = run.Style, Track = run.Track,
                    Stage = run.Stage, Time = time,
                };

                if (!onCurrent)
                {
                    _otherMaps.OnRunDeleted(map, record.Id);
                }
                else if (run.WasBest && player is not null)
                {
                    _playerCache.ReplaceRecord(player.Slot, run.Style, run.Track, run.Stage, null);
                }

                _runDeletionListener?.OnRunDeleted(map, onCurrent, record, run);
            }
        }
        else if (!confirmed && wiped is { Runs: > 0 })
        {
            _pendingWipes[admin] = (steamId, Environment.TickCount64 + WipeConfirmMilliseconds);
        }

        if (_bridge.ClientManager.GetGameClient(admin) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return;
        }

        var tr     = _localization.For(client.Slot);
        var target = Utils.Highlight(name);

        controller.PrintToChat(wiped is null            ? tr.Format(ChatTexts.WipeFailed, target)
                               : wiped.Runs == 0        ? tr.Format(ChatTexts.WipeNothing, target)
                               : !confirmed             ? tr.Format(ChatTexts.WipeConfirm, target, wiped.Runs, wiped.Maps,
                                                                    steamId.ToString(CultureInfo.InvariantCulture))
                                                          : tr.Format(ChatTexts.WipeDone, target, wiped.Runs, wiped.Maps));
    }

    // A SteamID, else a player on the server: an exact name (ignoring case), else the only name containing it.
    private ChatText? FindWipeTarget(string arg, out ulong steamId, out string name)
    {
        if (SteamIds.TryParse(arg, out steamId))
        {
            name = _bridge.ClientManager.GetGameClient(new SteamID(steamId))?.Name ?? arg;

            return null;
        }

        steamId = 0;
        name    = arg;
        var matches = 0;

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            if (client.Name.Equals(arg, StringComparison.OrdinalIgnoreCase))
            {
                (steamId, name) = (client.SteamId.AsPrimitive(), client.Name);

                return null;
            }

            if (client.Name.Contains(arg, StringComparison.OrdinalIgnoreCase))
            {
                (steamId, name) = (client.SteamId.AsPrimitive(), client.Name);
                matches++;
            }
        }

        return matches switch
        {
            1 => null,
            0 => ChatTexts.FindNone,
            _ => ChatTexts.FindMany,
        };
    }
}
