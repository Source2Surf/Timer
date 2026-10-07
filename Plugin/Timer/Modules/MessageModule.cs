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
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Timer;

namespace Source2Surf.Timer.Modules;

internal interface IMessageModule
{
}

internal class MessageModule : IModule, IMessageModule, IRecordModuleListener, ITimerModuleListener
{
    private readonly InterfaceBridge       _bridge;
    private readonly IRecordModule         _recordModule;
    private readonly ITimerModule          _timerModule;
    private readonly IStyleModule          _styleModule;
    private readonly IZoneModule           _zoneModule;
    private readonly ILocalizationProvider  _localization;
    private readonly IRequestManager        _request;
    private readonly ILogger<MessageModule> _logger;

    // The record cache keeps only the SR's checkpoint splits, so each player's PB splits are fetched when their run
    // starts, keyed by the PB they belong to.
    private readonly (int Style, int Track, long RecordId, IReadOnlyList<RunCheckpoint>? Checkpoints)[] _pbCheckpoints
        = new (int, int, long, IReadOnlyList<RunCheckpoint>?)[PlayerSlot.MaxPlayerCount];

    public MessageModule(InterfaceBridge        bridge,
                         IRecordModule          recordModule,
                         ITimerModule           timerModule,
                         IStyleModule           styleModule,
                         IZoneModule            zoneModule,
                         ILocalizationProvider  localization,
                         IRequestManager        request,
                         ILogger<MessageModule> logger)
    {
        _bridge       = bridge;
        _recordModule = recordModule;
        _timerModule  = timerModule;
        _styleModule  = styleModule;
        _zoneModule   = zoneModule;
        _localization = localization;
        _request      = request;
        _logger       = logger;
    }

    public bool Init()
    {
        _recordModule.RegisterListener(this);
        _timerModule.RegisterListener(this);

        return true;
    }

    public void Shutdown()
    {
        _recordModule.UnregisterListener(this);
        _timerModule.UnregisterListener(this);
    }

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        switch (recordEvent.RecordType)
        {
            case EAttemptResult.NewPersonalRecord:
            {
                PrintNewPersonalBestMessage(recordEvent);

                break;
            }
            case EAttemptResult.NewServerRecord:
            {
                PrintNewServerRecordMessage(recordEvent);

                break;
            }
            case EAttemptResult.NoNewRecord:
            {
                PrintNoNewRecordMessage(recordEvent);

                break;
            }
            default:
                throw new NotImplementedException($"Type {recordEvent.RecordType} is not implemented");
        }
    }

    public void OnReachCheckpoint(IPlayerController controller,
                                  IPlayerPawn       pawn,
                                  ITimerInfo        timerInfo,
                                  int               checkpoint)
    {
        var wr    = _recordModule.GetWRCheckpoints(timerInfo.Style, timerInfo.Track);
        var pb    = GetPbCheckpoints(controller.PlayerSlot, timerInfo.Style, timerInfo.Track);
        var index = checkpoint - 1;

        pawn.PrintToChat(CheckpointLine(_localization.For(controller.PlayerSlot),
                                        checkpoint,
                                        _zoneModule.GetLastCheckpoint(timerInfo.Track),
                                        timerInfo.Time,
                                        wr is not null && index >= 0 && index < wr.Count ? timerInfo.Time - wr[index].Time : null,
                                        pb is not null && index >= 0 && index < pb.Count ? timerInfo.Time - pb[index].Time : null,
                                        index >= 0 && index < timerInfo.Checkpoints.Count
                                            ? timerInfo.Checkpoints[index].EndVelocity.Length2D()
                                            : null));
    }

    /// <summary>
    ///     "CP 1/4 | 16.171 | SR -0.123 | PB +0.045 | 1290 u/s": grey, with its values in colour. Without an SR, a PB
    ///     or a speed, that part is left out.
    /// </summary>
    internal static string CheckpointLine(ChatTr tr, int checkpoint, int total, float time, float? vsSr, float? vsPb, float? speed)
    {
        var line = tr.Format(ChatTexts.Checkpoint, checkpoint, Math.Max(total, checkpoint), Utils.FormatTime(time, true));

        if (vsSr is { } sr)
        {
            line = ZString.Concat(line, tr.Format(ChatTexts.VsSr, Utils.SignedDelta(sr)));
        }

        if (vsPb is { } pb)
        {
            line = ZString.Concat(line, tr.Format(ChatTexts.VsPb, Utils.SignedDelta(pb)));
        }

        if (speed is { } s && float.IsFinite(s))
        {
            line = ZString.Concat(line, tr.Format(ChatTexts.CheckpointSpeed, (int) MathF.Round(s)));
        }

        return line;
    }

    public void OnPlayerTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (_recordModule.GetPlayerRecord(slot, timerInfo.Style, timerInfo.Track) is not { } pb
            || _pbCheckpoints[slot] is var cached && cached.Style == timerInfo.Style && cached.Track == timerInfo.Track && cached.RecordId == pb.Id)
        {
            return;
        }

        _pbCheckpoints[slot] = (timerInfo.Style, timerInfo.Track, pb.Id, null);
        _                    = LoadPbCheckpointsAsync(slot, timerInfo.Style, timerInfo.Track, pb.Id);
    }

    private IReadOnlyList<RunCheckpoint>? GetPbCheckpoints(PlayerSlot slot, int style, int track)
        => _pbCheckpoints[slot] is var cached
           && cached.Style == style
           && cached.Track == track
           && _recordModule.GetPlayerRecord(slot, style, track)?.Id == cached.RecordId
            ? cached.Checkpoints
            : null;

    private async Task LoadPbCheckpointsAsync(PlayerSlot slot, int style, int track, long recordId)
    {
        IReadOnlyList<RunCheckpoint>? checkpoints = null;

        try
        {
            checkpoints = await _request.GetRecordCheckpoints(recordId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to load PB checkpoints of record {recordId}", recordId);
        }

        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            if (_pbCheckpoints[slot] == (style, track, recordId, null))
            {
                // A failed load is forgotten, so the next start tries again.
                _pbCheckpoints[slot] = checkpoints is null ? default : (style, track, recordId, checkpoints);
            }
        }).ConfigureAwait(false);
    }

    // "New SR! Nuko finished Main - Normal in 25.421 (SR -02:18.093)"
    private void PrintNewServerRecordMessage(PlayerRecordSavedEvent recordEvent)
    {
        var record = recordEvent.SavedRecord;
        var style  = _styleModule.GetStyleSetting(record.Style).Name;
        var delta  = recordEvent.WrRecord is { } sr ? Improvement(sr.Time, record.Time) : null;

        _bridge.PrintToChatAll(_localization,
                                             tr => ZString.Concat(tr[ChatTexts.FinishSr],
                                                                  " ",
                                                                  Finished(tr, recordEvent.PlayerName, record, style),
                                                                  delta is null ? "" : tr.Format(ChatTexts.FinishVsSr, delta)));
    }

    // "Nuko finished Main - Normal in 01:02.345 (PB -0.512) #3/45"; a first finish has no PB to compare.
    // A stage PB goes to its player only.
    private void PrintNewPersonalBestMessage(PlayerRecordSavedEvent recordEvent)
    {
        var record        = recordEvent.SavedRecord;
        var style         = _styleModule.GetStyleSetting(record.Style).Name;
        var delta         = recordEvent.PbRecord is { } pb ? Improvement(pb.Time, record.Time) : null;
        var (rank, total) = recordEvent.IsStageRecord ? (0, 0) : GetRank(record, recordEvent.PbRecord is null);

        string Message(ChatTr tr)
            => ZString.Concat(Finished(tr, recordEvent.PlayerName, record, style),
                              delta is null ? "" : tr.Format(ChatTexts.FinishVsPb, delta),
                              rank > 0 ? tr.Format(ChatTexts.FinishRank, rank, total) : "");

        if (!recordEvent.IsStageRecord)
        {
            _bridge.PrintToChatAll(_localization, Message);
        }
        else if (FindPlayerControllerBySteamId(recordEvent.SteamId) is { IsValidEntity: true } controller)
        {
            controller.PrintToChat(Message(_localization.For(controller.PlayerSlot)));
        }
    }

    // To its player only: "Nuko finished Main - Normal in 01:03.000 (PB +0.655)"
    private void PrintNoNewRecordMessage(PlayerRecordSavedEvent recordEvent)
    {
        if (FindPlayerControllerBySteamId(recordEvent.SteamId) is not { IsValidEntity: true } controller)
        {
            return;
        }

        var record  = recordEvent.SavedRecord;
        var tr      = _localization.For(controller.PlayerSlot);
        var message = Finished(tr, recordEvent.PlayerName, record, _styleModule.GetStyleSetting(record.Style).Name);

        if (recordEvent.PbRecord is { } pb)
        {
            message += tr.Format(ChatTexts.FinishVsPb, Utils.SignedDelta(record.Time - pb.Time));
        }

        controller.PrintToChat(message);
    }

    private static string Finished(ChatTr tr, string playerName, RunRecord record, string style)
        => tr.Format(ChatTexts.Finish, playerName, Scope(tr, record), style, Utils.FormatTime(record.Time, true));

    private static string Scope(ChatTr tr, RunRecord record)
        => record.Stage <= 0 ? tr.Track(record.Track)
            : record.Track <= 0 ? tr.Format(ChatTexts.TrackStage, record.Stage)
                                  : tr.Format(ChatTexts.TrackBonusStage, record.Track, record.Stage);

    private static string Improvement(float previous, float time)
        => ZString.Concat(ChatColor.LightGreen, '-', Utils.FormatTime(MathF.Max(previous - time, 0f), true), ChatColor.White);

    // The leaderboard is refreshed after this message, so it doesn't hold the new run yet: a first finish adds one.
    private (int rank, int total) GetRank(RunRecord record, bool firstFinish)
    {
        try
        {
            return (_recordModule.GetRankForTime(record.Style, record.Track, record.Time),
                    _recordModule.GetTotalRecordCount(record.Style, record.Track) + (firstFinish ? 1 : 0));
        }
        catch (Exception)
        {
            // GetRankForTime may throw on out-of-bounds style/track
            return (0, 0);
        }
    }

    private IPlayerController? FindPlayerControllerBySteamId(SteamID steamId)
        => _bridge.ClientManager.GetGameClient(steamId)?.GetPlayerController();
}
