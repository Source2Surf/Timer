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
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Models;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class RecordModule
{
    public const string DeleteRecordsPermission = "timer:records";

    // Runs being deleted, so a double click sends one request.
    private readonly HashSet<long> _deletingRuns = [];

    public bool CanDeleteRecords(PlayerSlot slot)
        => _bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: false } client
           && _adminPermissions.HasPermission(client.SteamId, DeleteRecordsPermission);

    public void DeleteRecord(PlayerSlot slot, RunRecord record, string? map = null)
    {
        if (!CanDeleteRecords(slot) || !_deletingRuns.Add(record.Id))
        {
            return;
        }

        var admin      = _bridge.ClientManager.GetGameClient(slot)!.SteamId;
        var currentMap = IsCurrentMap(map);
        var mapName    = currentMap ? _bridge.CurrentMapName : map!;
        var load       = _mapCache.BeginLoad();

        _taskTracker.Track(Task.Run(async () =>
        {
            DeletedRun? deleted = null;
            var         failed  = false;

            try
            {
                deleted = await _recordAdministration.DeleteRunAsync(mapName, (ulong) record.Id).ConfigureAwait(false);

                if (currentMap && deleted is { WasBest: true })
                {
                    await _saver.RefreshBoardAsync(mapName, record.Style, record.Track, record.Stage, load).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                failed = true;
                _logger.LogError(e, "Failed to delete run {RunId} on {Map}", record.Id, mapName);
            }

            try
            {
                await _bridge.ModSharp.InvokeFrameActionAsync(() => OnRecordDeleted(admin, mapName, currentMap, load, record, deleted, failed))
                             .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, _bridge.CancellationToken));
    }

    private void OnRecordDeleted(SteamID admin, string mapName, bool currentMap, MapRecordCache.LoadToken load,
                                 RunRecord record, DeletedRun? deleted, bool failed)
    {
        _deletingRuns.Remove(record.Id);

        if (deleted is not null)
        {
            _logger.LogInformation("{Admin} deleted run {RunId} of {Player} ({SteamId}), {Time}s on {Map} style {Style} track {Track} stage {Stage}",
                                   admin, record.Id, record.PlayerName, record.SteamId, record.Time, mapName,
                                   record.Style, record.Track, record.Stage);

            // The server may have changed maps meanwhile.
            currentMap &= _mapCache.IsCurrent(load);

            if (!currentMap)
            {
                _otherMaps.OnRunDeleted(mapName, record.Id);
            }
            else if (deleted.WasBest && _bridge.ClientManager.GetGameClient(new SteamID(record.SteamId)) is { } player)
            {
                // The reloaded board holds their next-fastest run, if any.
                var next = GetRecords(record.Style, record.Track, record.Stage)
                    .FirstOrDefault(x => x.SteamId == record.SteamId && x.Id != record.Id);
                _playerCache.ReplaceRecord(player.Slot, record.Style, record.Track, record.Stage, next);
            }

            _runDeletionListener?.OnRunDeleted(mapName, currentMap, record, deleted);
        }

        if (_bridge.ClientManager.GetGameClient(admin) is not { } client
            || client.GetPlayerController() is not { IsValidEntity: true } controller)
        {
            return;
        }

        var tr = _localization.For(client.Slot);

        controller.PrintToChat(failed            ? tr[ChatTexts.RecordDeleteFailed]
                               : deleted is null ? tr[ChatTexts.RecordAlreadyDeleted]
                                                   : tr.Format(ChatTexts.RecordDeleted,
                                                               Utils.Highlight(record.PlayerName),
                                                               Utils.ColoredTime(record.Time)));
    }
}
