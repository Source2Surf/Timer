/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Units;
using SurfTimer.Managers;
using SurfTimer.Managers.Player;
using SurfTimer.Managers.Request.Models;
using SurfTimer.Modules.Timer;

namespace SurfTimer.Modules;

internal interface IRecordModule
{
    delegate void OnPlayerRecordSavedDelegate(SteamID        playerSteamId,
                                              string         playerName,
                                              EAttemptResult recordType,
                                              RunRecord      savedRecord,
                                              RunRecord?     wrRecord,
                                              RunRecord?     pbRecord);

    event OnPlayerRecordSavedDelegate OnPlayerRecordSaved;
    event OnPlayerRecordSavedDelegate OnPlayerStageRecordSaved;

    int GetRankForTime(int style, int track, float time);

    RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0);

    RunRecord? GetWR(int style, int track, int stage = 0);

    float? GetWRTime(int style, int track);
}

internal partial class RecordModule : IModule, IGameListener, IRecordModule
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge       _bridge;
    private readonly ITimerModule          _timerModule;
    private readonly IPlayerManager        _playerManager;
    private readonly ICommandManager       _commandManager;
    private readonly IRequestManager       _request;
    private readonly IMapInfoModule        _mapInfo;
    private readonly ILogger<RecordModule> _logger;

    /*private readonly ConcurrentDictionary<(int style, int track), List<float>> _mapRecordsCache = [];*/

    private readonly List<RunRecord>[,] _mapRecordsCache = new List<RunRecord>[Utils.MAX_STYLE, Utils.MAX_TRACK];

    private readonly List<RunRecord>[,,] _mapStageRecordsCache
        = new List<RunRecord>[Utils.MAX_STYLE, Utils.MAX_TRACK, Utils.MAX_STAGE];

    private readonly RunRecord?[,,] _playerRecordsCache
        = new RunRecord[PlayerSlot.MaxPlayerSlot, Utils.MAX_STYLE, Utils.MAX_TRACK];

    private readonly RunRecord?[,,,] _playerStageRecordsCache
        = new RunRecord?[PlayerSlot.MaxPlayerSlot, Utils.MAX_STYLE, Utils.MAX_TRACK, Utils.MAX_STAGE];

    public RecordModule(InterfaceBridge       bridge,
                        ITimerModule          timerModule,
                        IPlayerManager        playerManager,
                        IRequestManager       request,
                        ICommandManager       commandManager,
                        IMapInfoModule        mapInfoModule,
                        ILogger<RecordModule> logger)
    {
        _bridge         = bridge;
        _timerModule    = timerModule;
        _playerManager  = playerManager;
        _request        = request;
        _commandManager = commandManager;
        _mapInfo        = mapInfoModule;
        _logger         = logger;

        for (var s = 0; s < Utils.MAX_STYLE; s++)
        {
            for (var t = 0; t < Utils.MAX_TRACK; t++)
            {
                _mapRecordsCache[s, t] = [];
            }
        }

        for (var style = 0; style < Utils.MAX_STYLE; style++)
        {
            for (var track = 0; track < Utils.MAX_TRACK; track++)
            {
                for (var stage = 0; stage < Utils.MAX_STAGE; stage++)
                {
                    _mapStageRecordsCache[style, track, stage] = [];
                }
            }
        }

        for (var i = 0; i < PlayerSlot.MaxPlayerSlot; i++)
        {
            for (var j = 0; j < Utils.MAX_STYLE; j++)
            {
                for (var k = 0; k < Utils.MAX_TRACK; k++)
                {
                    for (var l = 0; l < Utils.MAX_STAGE; l++)
                    {
                        _playerStageRecordsCache[i, j, k, l] = null;
                    }

                    _playerRecordsCache[i, j, k] = null;
                }
            }
        }
    }

    public bool Init()
    {
        _bridge.ModSharp.InstallGameListener(this);

        _timerModule.OnPlayerFinishMap        += OnPlayerFinishMap;
        _timerModule.OnPlayerStageTimerFinish += OnPlayerStageTimerFinish;

        _playerManager.ClientPutInServer  += ClientPutInServer;
        _playerManager.ClientDisconnected += OnClientDisconnected;
        _playerManager.ClientInfoLoaded   += OnClientInfoLoaded;

#if DEBUG
        {
            _commandManager.AddServerCommand("clr_rec", OnCommandClearRecords);
        }
#endif

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);

        _timerModule.OnPlayerFinishMap        -= OnPlayerFinishMap;
        _timerModule.OnPlayerStageTimerFinish -= OnPlayerStageTimerFinish;

        _playerManager.ClientPutInServer  -= ClientPutInServer;
        _playerManager.ClientDisconnected -= OnClientDisconnected;
        _playerManager.ClientInfoLoaded   -= OnClientInfoLoaded;
    }

    public void OnGameActivate()
    {
    }

    public void OnGameInit()
    {
    }

    public void OnServerActivate()
    {
        Task.Run(async () =>
        {
            var currentMapInfo = _mapInfo.GetCurrentMapProfile();

            var records      = await _request.GetMapRecords(currentMapInfo.MapId).ConfigureAwait(false);
            var stageRecords = await _request.GetMapStageRecords(currentMapInfo.MapId).ConfigureAwait(false);

            foreach (var record in records)
            {
                var track = record.Track;
                var style = record.Style;

                _mapRecordsCache[style, track].Add(record);
            }

            foreach (var record in stageRecords)
            {
                var track = record.Track;
                var style = record.Style;
                var stage = record.Stage;

                _mapStageRecordsCache[style, track, stage].Add(record);
            }

            for (var style = 0; style < Utils.MAX_STYLE; style++)
            {
                for (var track = 0; track < Utils.MAX_TRACK; track++)
                {
                    for (var stage = 0; stage < Utils.MAX_STAGE; stage++)
                    {
                        _mapStageRecordsCache[style, track, stage].Sort();
                    }

                    _mapRecordsCache[style, track].Sort();
                }
            }
        });
    }

    public void OnGameShutdown()
    {
        for (var style = 0; style < Utils.MAX_STYLE; style++)
        {
            for (var track = 0; track < Utils.MAX_TRACK; track++)
            {
                for (var stage = 0; stage < Utils.MAX_STAGE; stage++)
                {
                    _mapStageRecordsCache[style, track, stage].Clear();
                }

                _mapRecordsCache[style, track].Clear();
            }
        }
    }

    private static RecordRequest CreateRecordRequest(ITimerInfo timerInfo)
    {
        var recordRequest = new RecordRequest
        {
            Style   = timerInfo.Style,
            Track   = timerInfo.Track,
            Stage   = 0, // Default to 0 for main map, can be overridden for stages.
            Time    = timerInfo.Time,
            Jumps   = timerInfo.Jumps,
            Strafes = timerInfo.Strafes,
            Sync    = timerInfo.Sync,
        };

        for (var i = 0; i < timerInfo.Checkpoints.Count; i++)
        {
            var cp = timerInfo.Checkpoints[i];

            var request = new RecordRequest.CheckpointRecord
            {
                CheckpointIndex = i,
                Time            = cp.Time,
                Sync            = cp.Sync,
            };

            request.SetAverageVelocity(cp.AverageVelocity);
            request.SetTouchVelocity(cp.EndVelocity);

            recordRequest.Checkpoints.Add(request);
        }

        return recordRequest;
    }

    private void OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        var player = _playerManager.GetPlayer(slot);

        if (player is null)
        {
            using var scope = _logger.BeginScope("OnPlayerFinishMap");

            _logger.LogError("player slot#{slot} has null GamePlayer???", slot);

            return;
        }

        var steamId    = player.SteamId;
        var playerName = player.Name;

        var style      = timerInfo.Style;
        var track      = timerInfo.Track;

        var record   = _mapRecordsCache[style, track];
        var wrRecord = record.Count > 0 ? record[0] : null;
        var pbRecord = _playerRecordsCache[slot, style, track];

        var currentMapProfile = _mapInfo.GetCurrentMapProfile();

        var recordRequest = CreateRecordRequest(timerInfo);

        Task.Run(async () =>
        {
            try
            {
                var (recordType, savedRecord) = await _request.AddPlayerRecord(player.DatabaseId,
                                                                               currentMapProfile.MapId,
                                                                               recordRequest)
                                                              .ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    OnPlayerRecordSaved?.Invoke(steamId,
                                                playerName,
                                                recordType,
                                                savedRecord,
                                                wrRecord,
                                                pbRecord);

                    if (recordType < EAttemptResult.NewPersonalRecord)
                    {
                        return;
                    }

                    if (_playerManager.GetPlayer(steamId) is { } client)
                    {
                        _logger.LogInformation("Found player {steamid}, setting record cache", steamId);
                        _playerRecordsCache[client.Slot, style, track] = savedRecord;
                    }
                }).ConfigureAwait(false);

                await UpdateMapRecord(style, track).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when saving record");
            }
        });
    }

    private void OnPlayerStageTimerFinish(IPlayerController controller,
                                          IPlayerPawn       pawn,
                                          IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        var player = _playerManager.GetPlayer(slot);

        if (player is null)
        {
            using var scope = _logger.BeginScope("OnPlayerStageTimerFinish");
            _logger.LogError("player slot#{slot} has null GamePlayer???", slot);

            return;
        }

        var steamId    = player.SteamId;
        var playerName = player.Name;

        var style = timerInfo.Style;
        var track = timerInfo.Track;
        var stage = timerInfo.Stage;

        var record   = _mapStageRecordsCache[style, track, stage];
        var wrRecord = record.Count > 0 ? record[0] : null;
        var pbRecord = _playerStageRecordsCache[slot, style, track, stage];

        var currentMapProfile = _mapInfo.GetCurrentMapProfile();

        var recordRequest = CreateRecordRequest(timerInfo);
        recordRequest.Stage = timerInfo.Stage;

        Task.Run(async () =>
        {
            var (recordType, savedRecord) = await _request.AddPlayerStageRecord(player.DatabaseId,
                                                                                currentMapProfile.MapId,
                                                                                recordRequest)
                                                          .ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                OnPlayerStageRecordSaved?.Invoke(steamId,
                                                 playerName,
                                                 recordType,
                                                 savedRecord,
                                                 wrRecord,
                                                 pbRecord);

                if (_playerManager.GetPlayer(steamId) is { } client)
                {
                    _playerStageRecordsCache[client.Slot, style, track, stage] = savedRecord;
                }
            }).ConfigureAwait(false);

            await UpdateMapStageRecord(style, track, stage).ConfigureAwait(false);
        });
    }

    private void ClientPutInServer(IGamePlayer player)
    {
        if (player.IsFakeClient)
        {
            return;
        }

        var slot = player.Slot;

        ClearPlayerRecord(slot);
    }

    private void OnClientDisconnected(IGamePlayer player)
    {
        if (player.IsFakeClient)
        {
            return;
        }

        var slot = player.Slot;

        ClearPlayerRecord(slot);
    }

    private void OnClientInfoLoaded(IGamePlayer player)
    {
        var currentMapInfo = _mapInfo.GetCurrentMapProfile();

        Task.Run(async () =>
        {
            try
            {
                var records = await _request.GetPlayerRecords(player.DatabaseId, currentMapInfo.MapId).ConfigureAwait(false);

                var stageRecords = await _request.GetPlayerStageRecords(player.DatabaseId, currentMapInfo.MapId)
                                                 .ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_bridge.ClientManager.GetGameClient(player.SteamId) is not { } client)
                    {
                        return;
                    }

                    foreach (var record in records)
                    {
                        var style = record.Style;
                        var track = record.Track;

                        _playerRecordsCache[client.Slot, style, track] = record;
                    }

                    foreach (var record in stageRecords)
                    {
                        var style = record.Style;
                        var track = record.Track;
                        var stage = record.Stage;

                        _playerStageRecordsCache[client.Slot, style, track, stage] = record;
                    }
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when loading player time for {stemaid}", player.SteamId);
            }
        });
    }

    public event IRecordModule.OnPlayerRecordSavedDelegate? OnPlayerRecordSaved;
    public event IRecordModule.OnPlayerRecordSavedDelegate? OnPlayerStageRecordSaved;

    public int GetRankForTime(int style, int track, float time)
    {
        var records = _mapRecordsCache[style, track];
        var count   = records.Count;

        var low  = 0;
        var high = count;

        while (low < high)
        {
            var mid = (int) ((uint) low + (uint) high) >> 1;

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

    public RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0)
    {
        switch (stage)
        {
            case <= 0:
            {
                if (_playerRecordsCache[slot, style, track] is { } rec)
                {
                    return rec;
                }

                return null;
            }
            case >= Utils.MAX_STAGE:
                throw new IndexOutOfRangeException($"Stage index is out of range [1, {Utils.MAX_STAGE}), current: {stage}");
            default:
            {
                if (_playerStageRecordsCache[slot, style, track, stage] is { } stageRec)
                {
                    return stageRec;
                }

                return null;
            }
        }
    }

    public RunRecord? GetWR(int style, int track, int stage = 0)
    {
        switch (stage)
        {
            case <= 0:
            {
                var records = _mapRecordsCache[style, track];

                return records.Count > 0 ? records[0] : null;
            }
            case >= Utils.MAX_STAGE:
                throw new IndexOutOfRangeException($"Stage index is out of range [1, {Utils.MAX_STAGE}), current: {stage}");
            default:
            {
                var records = _mapStageRecordsCache[style, track, stage];

                return records.Count > 0 ? records[0] : null;
            }
        }
    }

    public float? GetWRTime(int style, int track)
    {
        var rec = _mapRecordsCache[style, track];

        if (rec.Count > 0)
        {
            return rec[0].Time;
        }

        return null;
    }

    private async Task UpdateMapRecord(int style, int track)
    {
        try
        {
            var records = await _request.GetMapRecords(_mapInfo.GetCurrentMapProfile().MapId, style, track)
                                        .ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                _mapRecordsCache[style, track] = records;
                _mapRecordsCache[style, track].Sort();
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to update map record with style {s}, track: {t}", style, track);
        }
    }

    private async Task UpdateMapStageRecord(int style, int track, int stage)
    {
        var records = await _request.GetMapStageRecords(_mapInfo.GetCurrentMapProfile().MapId, style, track, stage)
                                    .ConfigureAwait(false);

        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            _mapStageRecordsCache[style, track, stage] = records;
            _mapStageRecordsCache[style, track, stage].Sort();
        });
    }

    private void ClearPlayerRecord(PlayerSlot slot)
    {
        for (var i = 0; i < Utils.MAX_STYLE; i++)
        {
            for (var j = 0; j < Utils.MAX_TRACK; j++)
            {
                for (var k = 0; k < Utils.MAX_STAGE; k++)
                {
                    _playerStageRecordsCache[slot, i, j, k] = null;
                }

                _playerRecordsCache[slot, i, j] = null;
            }
        }
    }
}
