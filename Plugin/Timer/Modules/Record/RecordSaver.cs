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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Units;
using Source2Surf.Timer.Backend.Rpc.Contracts;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules.Record;

internal sealed class RecordSaver
{
    private readonly InterfaceBridge                    _bridge;
    private readonly IRequestManager                    _request;
    private readonly MapRecordCache                     _mapCache;
    private readonly PlayerRecordCache                  _playerCache;
    private readonly ListenerHub<IRecordModuleListener> _listenerHub;
    private readonly ILogger                            _logger;
    private readonly RemoteRunSubmissionOptions         _remoteSubmissionOptions;
    private readonly RemoteRunSubmissionWriter          _remoteSubmissionWriter;
    private readonly ILocalizationProvider              _localization;
    private IRecordModuleListener?                        _lateReplayListener;

    public RecordSaver(InterfaceBridge                    bridge,
                       IRequestManager                    request,
                        MapRecordCache                     mapCache,
                        PlayerRecordCache                  playerCache,
                        ListenerHub<IRecordModuleListener> listenerHub,
                        RemoteRunSubmissionOptions         remoteSubmissionOptions,
                        RunSubmissionSender                remoteSubmissionSender,
                        ILocalizationProvider              localization,
                        ILogger                            logger)
    {
        ArgumentNullException.ThrowIfNull(remoteSubmissionOptions);
        ArgumentNullException.ThrowIfNull(remoteSubmissionSender);

        _bridge                   = bridge;
        _request                  = request;
        _mapCache                 = mapCache;
        _playerCache              = playerCache;
        _listenerHub              = listenerHub;
        _remoteSubmissionOptions  = remoteSubmissionOptions;
        _remoteSubmissionWriter   = new RemoteRunSubmissionWriter(remoteSubmissionSender);
        _localization             = localization;
        _logger                   = logger;
    }

    /// <summary>
    /// ReplayRecorder is resolved after RecordModule construction because it depends on
    /// IRecordModule. Old-map acknowledgements go only to this listener, never to map-local
    /// chat/cache consumers.
    /// </summary>
    internal void SetLateReplayListener(IRecordModuleListener replayListener)
        => _lateReplayListener = replayListener ?? throw new ArgumentNullException(nameof(replayListener));

    /// <summary>
    /// Creates only run facts: the backend applies the style's score factor.
    /// </summary>
    public static RecordRequest CreateRecordRequest(ITimerInfo timerInfo)
    {
        ArgumentNullException.ThrowIfNull(timerInfo);

        var recordRequest = new RecordRequest
        {
            Style       = timerInfo.Style,
            Track       = timerInfo.Track,
            Stage       = 0,
            Time        = timerInfo.Time,
            Jumps       = timerInfo.Jumps,
            Strafes     = timerInfo.Strafes,
            Sync        = timerInfo.Sync,
        };

        recordRequest.SetStartVelocity(timerInfo.StartVelocity);
        recordRequest.SetAverageVelocity(timerInfo.AvgVelocity);
        recordRequest.SetMaxVelocity(timerInfo.MaxVelocity);
        recordRequest.SetEndVelocity(timerInfo.EndVelocity);

        for (var i = 0; i < timerInfo.Checkpoints.Count; i++)
        {
            var cp = timerInfo.Checkpoints[i];

            var request = new RecordRequest.CheckpointRecord
            {
                CheckpointIndex = i + 1, Time = cp.Time, Sync = cp.Sync,
            };

            request.SetAverageVelocity(cp.AverageVelocity);
            request.SetMaxVelocity(cp.MaxVelocity);
            request.SetStartVelocity(cp.StartVelocity);
            request.SetEndVelocity(cp.EndVelocity);

            recordRequest.Checkpoints.Add(request);
        }

        return recordRequest;
    }

    public Task SaveMapRecordAsync(SteamID           steamId,
                                   string            playerName,
                                   string            mapName,
                                   ulong             mapId,
                                   ITimerInfo        timerInfo,
                                   int               attemptId,
                                   CancellationToken ct)
    {
        var mapLoad       = _mapCache.BeginLoad();
        var finishedAtUtc = DateTime.UtcNow;
        var recordRequest = CreateRecordRequest(timerInfo);

        // An async method runs through its first await on this finish callback. Build and
        // enqueue the immutable facts before returning to the game loop or scheduling UI.
        return CompleteRemoteSaveAsync(
            SaveRemoteMapRecordAsync(steamId, playerName, mapName, mapId, recordRequest,
                                     finishedAtUtc, mapLoad, attemptId, ct),
            steamId, "main", ct);
    }

    public Task SaveStageRecordAsync(SteamID           steamId,
                                     string            playerName,
                                     string            mapName,
                                     ulong             mapId,
                                     IStageTimerInfo   timerInfo,
                                     int               attemptId,
                                     CancellationToken ct)
    {
        var style = timerInfo.Style;
        var track = timerInfo.Track;
        var stage = timerInfo.Stage;
        var mapLoad = _mapCache.BeginLoad();

        if (!IsValidStageIndex(stage))
        {
            _logger.LogWarning("Ignore stage-finish with invalid stage index. style={style}, track={track}, stage={stage}",
                               style,
                               track,
                               stage);

            return Task.CompletedTask;
        }

        var finishedAtUtc = DateTime.UtcNow;
        var recordRequest = CreateRecordRequest(timerInfo);
        recordRequest.Stage = timerInfo.Stage;

        return CompleteRemoteSaveAsync(
            SaveRemoteStageRecordAsync(steamId, playerName, mapName, mapId, recordRequest,
                                       finishedAtUtc, mapLoad, attemptId, ct),
            steamId, "stage", ct);
    }

    private async Task CompleteRemoteSaveAsync(Task              saveTask,
                                               SteamID           steamId,
                                               string            runKind,
                                               CancellationToken ct)
    {
        try
        {
            await saveTask.ConfigureAwait(false);
        }
        catch (RunSubmissionEnqueueException exception)
        {
            _logger.LogError(exception,
                "Remote {RunKind} submission {SubmissionId} was not queued ({Disposition}).",
                runKind, exception.SubmissionId, exception.Disposition);
            await NotifyPlayerAsync(steamId,
                exception.Disposition == SubmissionSpoolEnqueueDisposition.CapacityExceeded
                    ? ChatTexts.SaveQueueFull
                    : ChatTexts.SaveNotQueued,
                ct).ConfigureAwait(false);
        }
        catch (RunSubmissionRejectedException exception)
        {
            _logger.LogWarning(exception,
                "Remote {RunKind} submission {SubmissionId} was permanently rejected by the backend.",
                runKind, exception.SubmissionId);
            await NotifyPlayerAsync(steamId, ChatTexts.SaveRejected, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Stopped waiting for remote {RunKind} submission for {SteamId}; an ambiguous in-memory entry is not acknowledged by this cancellation.",
                runKind, steamId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error when saving remote {RunKind} record for {SteamId}", runKind, steamId);
            await NotifyPlayerAsync(steamId, ChatTexts.SaveUnconfirmed, ct).ConfigureAwait(false);
        }
    }

    private async Task SaveRemoteMapRecordAsync(SteamID                    steamId,
                                                string                     playerName,
                                                string                     mapName,
                                                ulong                      mapId,
                                                RecordRequest              recordRequest,
                                                DateTime                   finishedAtUtc,
                                                MapRecordCache.LoadToken   mapLoad,
                                                int                        attemptId,
                                                CancellationToken          ct)
    {
        var request = RemoteRunSubmissionMapper.CreateMain(steamId.AsPrimitive(),
                                                            mapName,
                                                            recordRequest,
                                                            finishedAtUtc,
                                                            _remoteSubmissionOptions);

        // EnqueueAndWaitAsync inserts into the sender's in-memory queue before its first wait.
        // This task is intentionally started before any UI work so a pending notification never
        // races ahead of a submission that has not yet reached the queue.
        var acknowledgementTask = _remoteSubmissionWriter.EnqueueAndWaitAsync(request, ct);
        if (!acknowledgementTask.IsCompleted)
        {
            LogRemoteSubmissionPending(request.SubmissionId, "main");
        }

        var response = await acknowledgementTask.ConfigureAwait(false);
        _logger.LogInformation("Remote main submission {submissionId} was confirmed as {disposition}; run {runId} is canonical.",
                               request.SubmissionId,
                               response.Disposition,
                               response.RunId);
        if (mapId == 0)
        {
            _logger.LogWarning("Remote main submission {submissionId} was acknowledged, but no stable map id was captured; suppressing PlayerRecordSavedEvent and cache projection.",
                               request.SubmissionId);
            await RefreshMapRecord(mapName, recordRequest.Style, recordRequest.Track, mapLoad).ConfigureAwait(false);
            return;
        }

        var acknowledged = RemoteRunSubmissionMapper.ToAcknowledgedRun(request, response, playerName, mapId);
        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            if (!_mapCache.IsCurrent(mapLoad))
            {
                _logger.LogInformation("Remote main submission {submissionId} was acknowledged after the map changed; sending a replay-only event without updating current-map records.",
                                       request.SubmissionId);
                _lateReplayListener?.OnRecordSaved(new PlayerRecordSavedEvent(steamId,
                    playerName,
                    acknowledged.RecordType,
                    acknowledged.SavedRecord,
                    null,
                    null,
                    attemptId));
                return;
            }

            var currentRecords = _mapCache.GetRecords(recordRequest.Style, recordRequest.Track);
            var currentWrRecord = currentRecords.Count > 0 ? currentRecords[0] : null;
            var currentClient = _bridge.ClientManager.GetGameClient(steamId);
            var currentPbRecord = currentClient is null
                ? null
                : _playerCache.GetRecord(currentClient.Slot, recordRequest.Style, recordRequest.Track);

            var recordEvent = new PlayerRecordSavedEvent(steamId,
                                                          playerName,
                                                          acknowledged.RecordType,
                                                          acknowledged.SavedRecord,
                                                          currentWrRecord,
                                                          currentPbRecord,
                                                          attemptId);

            // ReplayRecorderModule correlates a late acknowledgement by this map id and attempt
            // id. A remote outage longer than its fallback TTL can still lose the replay artifact;
            // that limitation must not prevent the accepted score write from being published here.
            NotifyRecordSavedListeners(recordEvent);

            if (acknowledged.RecordType < EAttemptResult.NewPersonalRecord)
            {
                return;
            }

            if (currentClient is not null)
            {
                _playerCache.SetRecord(currentClient.Slot,
                                       recordRequest.Style,
                                       recordRequest.Track,
                                       acknowledged.SavedRecord);
            }
        }, ct).ConfigureAwait(false);

        await RefreshMapRecord(mapName, recordRequest.Style, recordRequest.Track, mapLoad).ConfigureAwait(false);
    }

    private async Task SaveRemoteStageRecordAsync(SteamID                    steamId,
                                                  string                     playerName,
                                                  string                     mapName,
                                                  ulong                      mapId,
                                                  RecordRequest              recordRequest,
                                                  DateTime                   finishedAtUtc,
                                                  MapRecordCache.LoadToken   mapLoad,
                                                  int                        attemptId,
                                                  CancellationToken          ct)
    {
        var request = RemoteRunSubmissionMapper.CreateStage(steamId.AsPrimitive(),
                                                             mapName,
                                                             recordRequest,
                                                             finishedAtUtc,
                                                             _remoteSubmissionOptions);

        var acknowledgementTask = _remoteSubmissionWriter.EnqueueAndWaitAsync(request, ct);
        if (!acknowledgementTask.IsCompleted)
        {
            LogRemoteSubmissionPending(request.SubmissionId, "stage");
        }

        var response = await acknowledgementTask.ConfigureAwait(false);
        _logger.LogInformation("Remote stage submission {submissionId} was confirmed as {disposition}; run {runId} is canonical.",
                               request.SubmissionId,
                               response.Disposition,
                               response.RunId);
        if (mapId == 0)
        {
            _logger.LogWarning("Remote stage submission {submissionId} was acknowledged, but no stable map id was captured; suppressing PlayerRecordSavedEvent and cache projection.",
                               request.SubmissionId);
            await RefreshMapStageRecord(mapName,
                                        recordRequest.Style,
                                        recordRequest.Track,
                                        recordRequest.Stage,
                                        mapLoad).ConfigureAwait(false);
            return;
        }

        var acknowledged = RemoteRunSubmissionMapper.ToAcknowledgedRun(request, response, playerName, mapId);
        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            if (!_mapCache.IsCurrent(mapLoad))
            {
                _logger.LogInformation("Remote stage submission {submissionId} was acknowledged after the map changed; sending a replay-only event without updating current-map records.",
                                       request.SubmissionId);
                _lateReplayListener?.OnRecordSaved(new PlayerRecordSavedEvent(steamId,
                    playerName,
                    acknowledged.RecordType,
                    acknowledged.SavedRecord,
                    null,
                    null,
                    attemptId));
                return;
            }

            var currentStageRecords = _mapCache.GetStageRecords(recordRequest.Style,
                                                                 recordRequest.Track,
                                                                 recordRequest.Stage);
            var currentWrRecord = currentStageRecords is { Count: > 0 } ? currentStageRecords[0] : null;
            var currentClient = _bridge.ClientManager.GetGameClient(steamId);
            var currentPbRecord = currentClient is null
                ? null
                : _playerCache.GetRecord(currentClient.Slot,
                                         recordRequest.Style,
                                         recordRequest.Track,
                                         recordRequest.Stage);

            var recordEvent = new PlayerRecordSavedEvent(steamId,
                                                          playerName,
                                                          acknowledged.RecordType,
                                                          acknowledged.SavedRecord,
                                                          currentWrRecord,
                                                          currentPbRecord,
                                                          attemptId);
            NotifyRecordSavedListeners(recordEvent);

            if (currentClient is not null)
            {
                _playerCache.SetStageRecord(currentClient.Slot,
                                            recordRequest.Style,
                                            recordRequest.Track,
                                            recordRequest.Stage,
                                            acknowledged.SavedRecord);
            }
        }, ct).ConfigureAwait(false);

        await RefreshMapStageRecord(mapName,
                                    recordRequest.Style,
                                    recordRequest.Track,
                                    recordRequest.Stage,
                                    mapLoad).ConfigureAwait(false);
    }

    private void LogRemoteSubmissionPending(Guid submissionId, string runKind)
        => _logger.LogInformation("Queued remote {runKind} submission {submissionId}; awaiting canonical backend acknowledgement.",
                                  runKind,
                                  submissionId);

    private Task NotifyPlayerAsync(SteamID steamId, ChatText text, CancellationToken ct)
        => NotifyPlayerAsync(steamId, tr => tr[text], ct);

    // The message is made on the game thread, in the player's language.
    private async Task NotifyPlayerAsync(SteamID              steamId,
                                         Func<ChatTr, string> message,
                                         CancellationToken    ct)
    {
        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            if (_bridge.ClientManager.GetGameClient(steamId) is { } client)
            {
                client.GetPlayerController()?.PrintToChat(message(_localization.For(client.Slot)));
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task RefreshMapRecord(string mapName, int style, int track, MapRecordCache.LoadToken origin)
    {
        if (!_mapCache.IsCurrent(origin)) return;
        var load = _mapCache.BeginLoad(origin);
        try
        {
            var records = await RetryHelper.RetryAsync(
                () => _request.GetMapRecords(mapName, style, track),
                RetryHelper.IsTransient, _logger, "GetMapRecords"
            ).ConfigureAwait(false);

            IReadOnlyList<RunCheckpoint>? wrCheckpoints = null;

            if (records.Count > 0)
            {
                wrCheckpoints = await RetryHelper.RetryAsync(
                    () => _request.GetRecordCheckpoints(records[0].Id),
                    RetryHelper.IsTransient, _logger, "GetRecordCheckpoints"
                ).ConfigureAwait(false);
            }

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                _mapCache.RefreshTrack(style, track, records, load);

                if (wrCheckpoints is not null)
                {
                    _mapCache.SetWRCheckpoints(style, track, wrCheckpoints, load);
                }
                else
                {
                    _mapCache.SetWRCheckpoints(style, track, [], load);
                }
            });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to update map record with style {s}, track: {t}", style, track);
        }
    }

    private async Task RefreshMapStageRecord(string mapName, int style, int track, int stage, MapRecordCache.LoadToken origin)
    {
        if (!_mapCache.IsCurrent(origin)) return;
        var load = _mapCache.BeginLoad(origin);
        if (!IsValidStageIndex(stage))
        {
            _logger.LogWarning("Skip RefreshMapStageRecord with invalid stage index. style={style}, track={track}, stage={stage}",
                               style,
                               track,
                               stage);

            return;
        }

        try
        {
            var records = await RetryHelper.RetryAsync(
                () => _request.GetMapStageRecords(mapName, style, track, stage),
                RetryHelper.IsTransient, _logger, "GetMapStageRecords"
            ).ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() => { _mapCache.RefreshStage(style, track, stage, records, load); });
        }
        catch (Exception e)
        {
            _logger.LogError(e,
                             "Error when trying to update map stage record with style {s}, track: {t}, stage: {st}",
                             style,
                             track,
                             stage);
        }
    }

    private void NotifyRecordSavedListeners(PlayerRecordSavedEvent recordEvent)
        => _listenerHub.NotifyAll("OnRecordSaved",
                                  static (l, e) => l.OnRecordSaved(e),
                                  recordEvent);

    private static bool IsValidStageIndex(int stage) =>
        stage is >= 1 and < TimerConstants.MAX_STAGE;
}
