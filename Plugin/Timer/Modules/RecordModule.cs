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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Command;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Managers.Request;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Modules.Practice;
using Source2Surf.Timer.Modules.Record;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal interface IRecordModule
{
    void RegisterListener(IRecordModuleListener listener);

    void UnregisterListener(IRecordModuleListener listener);

    int GetRankForTime(int style, int track, float time);

    RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0);

    RunRecord? GetWR(int style, int track, int stage = 0);

    float? GetWRTime(int style, int track);

    int GetTotalRecordCount(int style, int track);

    /// <summary>
    ///     A leaderboard, fastest first: the map's (stage 0) or one stage's.
    /// </summary>
    IReadOnlyList<RunRecord> GetRecords(int style, int track, int stage);

    IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track);

    /// <summary>
    /// Get the current session elapsed time (seconds) for a player on this map.
    /// Returns 0 if the player has no active session.
    /// </summary>
    float GetSessionTime(PlayerSlot slot);

    // Raised by !wr and !sr [map], to open the leaderboard panel: null for the current map, else the map's name.
    event Action<PlayerSlot, string?>? LeaderboardRequested;

    // Changes whenever a leaderboard is loaded, reloaded or cleared, of this map or another.
    int RecordsVersion { get; }

    /// <summary>
    /// A board of any map, fastest first. Another map's are null while it loads (the first read starts the load) and
    /// are reloaded in the background once a minute old; the current map's are the ones above.
    /// </summary>
    IReadOnlyList<RunRecord>? GetRecords(string map, int style, int track, int stage);

    // The boards with records, by style, track then stage (0 = the map); null while another map loads.
    IReadOnlyList<(int Style, int Track, int Stage)>? GetBoards(string map);

    // Admins with timer:records.
    bool CanDeleteRecords(PlayerSlot slot);

    /// <summary>
    /// Deletes a run and its replays, for an admin who <see cref="CanDeleteRecords" />: of the current map, or of
    /// <paramref name="map" />. When it was the player's best, their next-fastest run takes its place or they leave
    /// the board; the admin gets a chat line.
    /// </summary>
    void DeleteRecord(PlayerSlot slot, RunRecord record, string? map = null);
}

internal partial class RecordModule : IModule, IGameListener, IRecordModule, ITimerModuleListener, IPlayerManagerListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge       _bridge;
    private readonly ITimerModule          _timerModule;
    private readonly IPlayerManager        _playerManager;
    private readonly ICommandManager       _commandManager;
    private readonly IRequestManager       _request;
    private readonly IMapInfoModule        _mapInfo;
    private readonly IPracticeModule       _practiceModule;
    private readonly ILocalizationProvider _localization;
    private readonly IAdminPermissions     _adminPermissions;
    private readonly IRecordAdministration _recordAdministration;
    private readonly ILogger<RecordModule> _logger;

    // Sub-components
    private readonly MapRecordCache                     _mapCache;
    private readonly OtherMapRecords                    _otherMaps;
    private readonly PlayerRecordCache                  _playerCache;
    private readonly RecordSaver                        _saver;
    private readonly TaskTracker                        _taskTracker;
    private readonly ListenerHub<IRecordModuleListener> _listenerHub;

    // Per-slot session start time (engine time when player joined this map)
    private readonly double[] _sessionStartTime = new double[PlayerSlot.MaxPlayerCount];

    // Late-resolved to avoid circular DI (ReplayRecorderModule depends on IRecordModule)
    private IReplayRecorderModule _replayRecorder = null!;
    private IRunDeletionListener? _runDeletionListener;

    public RecordModule(InterfaceBridge       bridge,
                        ITimerModule          timerModule,
                        IPlayerManager        playerManager,
                        IRequestManager       request,
                        ICommandManager       commandManager,
                        IMapInfoModule        mapInfoModule,
                        IPracticeModule       practiceModule,
                        IConfiguration        configuration,
                        RunSubmissionSender   remoteSubmissionSender,
                        ILocalizationProvider localization,
                        IAdminPermissions     adminPermissions,
                        IRecordAdministration recordAdministration,
                        ILogger<RecordModule> logger)
    {
        _bridge               = bridge;
        _timerModule          = timerModule;
        _playerManager        = playerManager;
        _request              = request;
        _commandManager       = commandManager;
        _mapInfo              = mapInfoModule;
        _practiceModule       = practiceModule;
        _localization         = localization;
        _adminPermissions     = adminPermissions;
        _recordAdministration = recordAdministration;
        _logger               = logger;

        _listenerHub = new ListenerHub<IRecordModuleListener>(logger);
        _mapCache    = new MapRecordCache(logger);
        _playerCache = new PlayerRecordCache(logger);
        _saver       = new RecordSaver(bridge,
                                       request,
                                       _mapCache,
                                       _playerCache,
                                       _listenerHub,
                                       RemoteRunSubmissionOptions.FromConfiguration(configuration),
                                       remoteSubmissionSender,
                                       localization,
                                       logger);
        _taskTracker = new TaskTracker(logger);
        _otherMaps   = new OtherMapRecords(map => Task.Run(async () => (await request.GetMapRecords(map).ConfigureAwait(false),
                                                                        await request.GetMapStageRecords(map).ConfigureAwait(false))),
                                           action => bridge.ModSharp.InvokeFrameActionAsync(action),
                                           () => Environment.TickCount64,
                                           _taskTracker.Track,
                                           logger);
    }

    public bool Init()
    {
        _bridge.ModSharp.InstallGameListener(this);

        _timerModule.RegisterListener(this);

        _playerManager.RegisterListener(this);

        _commandManager.AddServerCommand("timer_recalc_scores", OnCommandRecalcScores);
        _adminPermissions.RegisterPermission(DeleteRecordsPermission);
        _commandManager.AddAdminChatCommand("wipeplayer", [DeleteRecordsPermission], OnCommandWipePlayer);

        _commandManager.AddClientChatCommand("wr",      OnCommandWR);
        _commandManager.AddClientChatCommand("sr",      OnCommandWR);
        _commandManager.AddClientChatCommand("pb",      OnCommandPB);
        _commandManager.AddClientChatCommand("rank",    OnCommandRank);
        _commandManager.AddClientChatCommand("top",     OnCommandTop);
        _commandManager.AddClientChatCommand("recent",  OnCommandRecent);
        _commandManager.AddClientChatCommand("cpr",      OnCommandCpr);
        _commandManager.AddClientChatCommand("swr",      OnCommandStageWR);
        _commandManager.AddClientChatCommand("stagewr",  OnCommandStageWR);
        _commandManager.AddClientChatCommand("btop",     OnCommandBonusTop);
        _commandManager.AddClientChatCommand("bwr",      OnCommandBonusWR);
        _commandManager.AddClientChatCommand("bpb",      OnCommandBonusPB);
        _commandManager.AddClientChatCommand("spb",      OnCommandStagePB);

#if DEBUG
        {
            _commandManager.AddServerCommand("clr_rec", OnCommandClearRecords);
        }
#endif

        return true;
    }

    public void OnPostInit(ServiceProvider provider)
    {
        _replayRecorder      = provider.GetRequiredService<IReplayRecorderModule>();
        _runDeletionListener = provider.GetService<IRunDeletionListener>();
        if (_replayRecorder is IRecordModuleListener replayListener)
        {
            _saver.SetLateReplayListener(replayListener);
        }
        else
        {
            _logger.LogError("Replay recorder does not implement IRecordModuleListener; old-map remote acknowledgements cannot attach replays.");
        }
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);

        _timerModule.UnregisterListener(this);

        _playerManager.UnregisterListener(this);

        _taskTracker.DrainPendingTasks();
    }

    public void OnGameActivate()
    {
    }

    public void OnGameInit()
    {
    }

    public void OnServerActivate()
    {
        var currentMapName = _bridge.CurrentMapName;
        _mapCache.Clear();
        _playerCache.ClearAll();
        var load = _mapCache.BeginLoad();

        Task.Run(async () =>
        {
            try
            {
                var records = await RetryHelper.RetryAsync(
                    () => _request.GetMapRecords(currentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapRecords"
                ).ConfigureAwait(false);

                var stageRecords = await RetryHelper.RetryAsync(
                    () => _request.GetMapStageRecords(currentMapName),
                    RetryHelper.IsTransient, _logger, "GetMapStageRecords"
                ).ConfigureAwait(false);

                // Load WR checkpoints for each (style, track) combination
                var wrPerTrack = records
                                 .GroupBy(r => (r.Style, r.Track))
                                 .Select(g => g.OrderBy(r => r.Time).ThenBy(r => r.Id).First());

                var wrCheckpointTasks = wrPerTrack.Select(async wr =>
                {
                    var checkpoints = await RetryHelper.RetryAsync(() => _request.GetRecordCheckpoints(wr.Id),
                                                                   RetryHelper.IsTransient,
                                                                   _logger,
                                                                   "GetRecordCheckpoints").ConfigureAwait(false);

                    return (key: (wr.Style, wr.Track), checkpoints);
                });

                var wrCheckpointResults = await Task.WhenAll(wrCheckpointTasks).ConfigureAwait(false);

                var wrCheckpointMap = wrCheckpointResults
                    .ToDictionary(r => r.key, r => r.checkpoints);

                var boards = _mapCache.Group(records, stageRecords);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (!_mapCache.IsCurrent(load)) return;
                    _mapCache.Populate(boards, load);

                    foreach (var ((style, track), checkpoints) in wrCheckpointMap)
                    {
                        _mapCache.SetWRCheckpoints(style, track, checkpoints, load);
                    }

                    _listenerHub.NotifyAll("OnMapRecordsLoaded", static l => l.OnMapRecordsLoaded());
                });
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when loading map records on server activate");
            }
        }, _bridge.CancellationToken);
    }

    public void OnGameShutdown()
    {
        // Flush playtime for all connected players before map change
        for (PlayerSlot i = 0; i < PlayerSlot.MaxPlayerCount; i++)
        {
            if (_sessionStartTime[i] <= 0)
            {
                continue;
            }

            if (_bridge.ClientManager.GetGameClient(i) is { IsFakeClient: false } client)
            {
                FlushPlayerMapStats(i, client.SteamId);
            }
            else
            {
                _sessionStartTime[i] = 0;
            }
        }

        _mapCache.Clear();
        _playerCache.ClearAll();
    }

    public void OnPlayerFinishMap(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            using var scope = _logger.BeginScope("OnPlayerFinishMap");

            _logger.LogError("player slot#{slot} has null IGameClient???", slot);

            return;
        }

        if (_practiceModule.IsInPractice(slot))
        {
            controller.PrintToChat(_localization.For(slot)[ChatTexts.PracticeRun]);
            return;
        }

        var mapName = _bridge.CurrentMapName;
        var mapId = CaptureFinishMapId(mapName);
        _taskTracker.Track(_saver.SaveMapRecordAsync(client.SteamId,
                                                     client.Name,
                                                     mapName,
                                                     mapId,
                                                     timerInfo,
                                                     attemptId: _replayRecorder.GetAttemptId(slot),
                                                     _bridge.CancellationToken));
    }

    public void OnPlayerStageTimerFinish(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        var client = _bridge.ClientManager.GetGameClient(slot);

        if (client is null)
        {
            using var scope = _logger.BeginScope("OnPlayerStageTimerFinish");
            _logger.LogError("player slot#{slot} has null IGameClient???", slot);

            return;
        }

        if (_practiceModule.IsInPractice(slot))
        {
            // Don't spam chat per-stage during practice; OnPlayerFinishMap already prints once.
            return;
        }

        var mapName = _bridge.CurrentMapName;
        var mapId = CaptureFinishMapId(mapName);
        _taskTracker.Track(_saver.SaveStageRecordAsync(client.SteamId,
                                                       client.Name,
                                                       mapName,
                                                       mapId,
                                                       timerInfo,
                                                       attemptId: _replayRecorder.GetAttemptId(slot),
                                                       _bridge.CancellationToken));
    }

    public void OnClientPutInServer(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is { IsFakeClient: true })
        {
            return;
        }

        _playerCache.Clear(slot);
        _sessionStartTime[slot] = _bridge.ModSharp.EngineTime();
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false } client)
        {
            return;
        }

        FlushPlayerMapStats(slot, client.SteamId);
        _sessionStartTime[slot] = 0; // player left, clear session

        _playerCache.Clear(slot);
    }

    public void OnClientInfoLoaded(SteamID steamId)
    {
        var client = _bridge.ClientManager.GetGameClient(steamId);

        if (client is null || client.IsFakeClient)
        {
            return;
        }

        var mapName = _bridge.CurrentMapName;
        var load = _mapCache.BeginLoad();

        _taskTracker.Track(Task.Run(async () =>
                                    {
                                        try
                                        {
                                            var records = await RetryHelper
                                                                .RetryAsync(() => _request.GetPlayerRecords(steamId, mapName),
                                                                            RetryHelper.IsTransient,
                                                                            _logger,
                                                                            "GetPlayerRecords").ConfigureAwait(false);

                                            var stageRecords = await RetryHelper.RetryAsync(
                                                () => _request.GetPlayerStageRecords(steamId, mapName),
                                                RetryHelper.IsTransient, _logger, "GetPlayerStageRecords"
                                            ).ConfigureAwait(false);

                                            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                            {
                                                if (!_mapCache.IsCurrent(load)) return;
                                                if (_bridge.ClientManager.GetGameClient(steamId)
                                                    is not { } currentClient)
                                                {
                                                    return;
                                                }

                                                _playerCache.Populate(currentClient.Slot, records, stageRecords);
                                            });
                                        }
                                        catch (Exception e)
                                        {
                                            _logger.LogError(e, "Error when loading player time for {steamId}", steamId);
                                        }
                                    },
                                    _bridge.CancellationToken));
    }

    public void RegisterListener(IRecordModuleListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IRecordModuleListener listener)
        => _listenerHub.Unregister(listener);

    // IRecordModule delegation to sub-components

    public int GetRankForTime(int style, int track, float time) =>
        _mapCache.GetRankForTime(style, track, time);

    public RunRecord? GetPlayerRecord(PlayerSlot slot, int style, int track, int stage = 0) =>
        _playerCache.GetRecord(slot, style, track, stage);

    public RunRecord? GetWR(int style, int track, int stage = 0) =>
        _mapCache.GetWR(style, track, stage);

    public float? GetWRTime(int style, int track) =>
        _mapCache.GetWRTime(style, track);

    public int GetTotalRecordCount(int style, int track) =>
        _mapCache.GetRecords(style, track).Count;

    public IReadOnlyList<RunRecord> GetRecords(int style, int track, int stage) =>
        stage == 0 ? _mapCache.GetRecords(style, track) : _mapCache.GetStageRecords(style, track, stage) ?? [];

    public IReadOnlyList<RunCheckpoint>? GetWRCheckpoints(int style, int track) =>
        _mapCache.GetWRCheckpoints(style, track);

    public float GetSessionTime(PlayerSlot slot)
    {
        var start = _sessionStartTime[slot];

        return start > 0 ? (float)(_bridge.ModSharp.EngineTime() - start) : 0f;
    }

    private void FlushPlayerMapStats(PlayerSlot slot, SteamID steamId)
    {
        var start = _sessionStartTime[slot];

        if (start <= 0)
        {
            return;
        }

        var delta   = (float)(_bridge.ModSharp.EngineTime() - start);
        var mapName = _bridge.CurrentMapName;

        _sessionStartTime[slot] = _bridge.ModSharp.EngineTime(); // reset for next session segment

        if (delta <= 0f)
        {
            return;
        }

        _taskTracker.Track(Task.Run(async () =>
        {
            try
            {
                await _request.UpdatePlayerMapStatsAsync(steamId, mapName, delta).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when flushing player map stats for {steamId}", steamId);
            }
        }, _bridge.CancellationToken));
    }

    /// <summary>
    ///     ServerCommand: timer_recalc_scores [mapname|all]
    ///     Queues score recalculation on the backend, under its score policy. No args = current map, with arg =
    ///     specified map, "all" = every map.
    /// </summary>
    private ECommandAction OnCommandRecalcScores(StringCommand arg)
    {
        var target  = arg.ArgCount > 1 ? arg.GetArg(1) : _bridge.CurrentMapName;
        var mapName = string.Equals(target, "all", StringComparison.OrdinalIgnoreCase) ? null : target;

        Task.Run(async () =>
                 {
                     try
                     {
                         var result = await RetryHelper.RetryAsync(
                             () => _request.RecalculateMapScoresAsync(mapName),
                             RetryHelper.IsTransient, _logger, "RecalculateMapScoresAsync"
                         ).ConfigureAwait(false);

                         if (!result.MapFound)
                         {
                             _logger.LogWarning("timer_recalc_scores: map '{map}' was not found.", target);

                             return;
                         }

                         _logger.LogInformation("Queued score recalculation for {target}: {boards} board(s) across {maps} map(s)",
                                                mapName ?? "every map",
                                                result.BoardsQueued,
                                                result.MapsAffected);

                         foreach (var failure in result.FailedMaps)
                         {
                             _logger.LogWarning("timer_recalc_scores: not queued: {failure}", failure);
                         }
                     }
                     catch (Exception e)
                     {
                         _logger.LogError(e, "Error when recalculating scores");
                     }
                 },
                 _bridge.CancellationToken);

        return ECommandAction.Handled;
    }

    private ulong CaptureFinishMapId(string mapName)
    {
        var profile = _mapInfo.GetCurrentMapProfile();
        if (profile.MapId != 0
            && string.Equals(profile.MapName, mapName, StringComparison.OrdinalIgnoreCase))
        {
            return profile.MapId;
        }

        _logger.LogWarning("No stable map identity was available at finish for {mapName}; a remote acknowledgement will not publish a local record event.",
                           mapName);
        return 0;
    }
}
