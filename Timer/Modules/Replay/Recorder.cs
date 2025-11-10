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
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using SurfTimer.Extensions;
using SurfTimer.Managers;
using SurfTimer.Managers.Request.Models;
using SurfTimer.Modules.Replay;
using SurfTimer.Modules.Timer;
using ZstdSharp;
using ZstdSharp.Unsafe;

// ReSharper disable CheckNamespace
namespace SurfTimer.Modules;
// ReSharper restore CheckNamespace

internal partial class ReplayModule
{
    private void OnTimerStart(IPlayerController controller, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (_playerFrameData[slot] is not { } frameData)
        {
            return;
        }

        if (frameData.IsSavingReplay)
        {
            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                _timerModule.StopTimer(slot);
                controller.PrintToChat("Timer stopped: Replay is saving.");
            });

            return;
        }

        if (frameData.GrabbingPostFrame)
        {
            frameData.GrabbingPostFrame = false;
            SaveReplayToFile(slot, frameData);

            if (frameData.PostFrameTimer is { } timer)
            {
                _bridge.ModSharp.StopTimer(timer);
            }

            frameData.PostFrameTimer = null;

            _bridge.ModSharp.InvokeFrameAction(() =>
            {
                _timerModule.StopTimer(slot);
                controller.PrintToChat("Timer stopped: Replay is saving.");
            });

            return;
        }

        var maxPreFrmae = (int) (timer_replay_prerun_time.GetFloat() * Utils.Tickrate);

        var delta = frameData.Frames.Count - maxPreFrmae;

        if (delta > 0)
        {
            frameData.Frames.RemoveRange(0, delta);
        }

        frameData.NewStageTicks.Clear();
        frameData.StageTimerStartTicks.Clear();

        frameData.TimerStartFrame = frameData.Frames.Count;
    }

    private void OnPlayerStageTimerStart(IPlayerController controller,
                                         IPlayerPawn       pawn,
                                         IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (_playerFrameData[slot] is not { } frameData)
        {
            return;
        }

        var stage = timerInfo.Stage;
        var idx   = stage - 1;

        var ticksList = frameData.StageTimerStartTicks;
        var count     = ticksList.Count;

        if (count == idx)
        {
            ticksList.Add(frameData.Frames.Count);
        }
        else if (idx < count)
        {
            ticksList[idx] = frameData.Frames.Count;
        }
        else
        {
            using var scope = _logger.BeginScope("OnPlayerStageTimerStart");

            _logger.LogError("Attempted to add CurrentFrame to StageTimerStartTick for stage {Stage} (index {Index}) "
                             + "when current stage count is {Count}. Probable logic error elsewhere.",
                             stage,
                             idx,
                             count);
        }
    }

    private void OnPlayerStageTimerFinish(IPlayerController controller,
                                          IPlayerPawn       pawn,
                                          IStageTimerInfo   timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        frame.NewStageTicks.Add(frame.Frames.Count);

        frame.Name = controller.PlayerName;
        var finishedStage = timerInfo.Stage;

        var lastStage = finishedStage - 1;

        var time = timerInfo.Time;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            var timerStartTick = frame.StageTimerStartTicks[lastStage];
            var newStageTicks  = frame.NewStageTicks[lastStage];

            var delay              = timer_replay_stage_postrun_time.GetFloat();
            var postRunFrameLength = (int) (Utils.Tickrate * delay);
            var preRunFrameLength  = (int) (Utils.Tickrate * timer_replay_stage_prerun_time.GetFloat());

            if (frame.StagePostFrameTimer is { } stageReplayTimer)
            {
                // we have ForceCallOnStop flag which forces firing the callback
                _bridge.ModSharp.StopTimer(stageReplayTimer);
            }

            frame.StagePostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                                   {
                                                                       var startTick = Math.Max(0,
                                                                                timerStartTick - preRunFrameLength);

                                                                       SaveStageReplayToFile(frame,
                                                                                startTick,
                                                                                timerStartTick,
                                                                                newStageTicks,
                                                                                postRunFrameLength,
                                                                                finishedStage,
                                                                                time);

                                                                       return TimerAction.Stop;
                                                                   },
                                                                   delay,
                                                                   GameTimerFlags.StopOnMapEnd
                                                                   | GameTimerFlags.ForceCallOnStop);
        });
    }

    private void OnPlayerFinishMap(IPlayerController controller,
                                   IPlayerPawn       pawn,
                                   ITimerInfo        timerInfo)
    {
        var slot = controller.PlayerSlot;

        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        frame.Name              = controller.PlayerName;
        frame.TimerFinishFrame  = frame.Frames.Count;
        frame.GrabbingPostFrame = true;
        frame.FinishTime        = timerInfo.Time;
        frame.Style             = timerInfo.Style;
        frame.Track             = timerInfo.Track;

        frame.PostFrameTimer = _bridge.ModSharp.PushTimer(() =>
                                                          {
                                                              frame.PostFrameTimer    = null;
                                                              frame.GrabbingPostFrame = false;

                                                              if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
                                                              {
                                                                  _bridge.ModSharp.StopTimer(stagePostFrameTimer);
                                                              }

                                                              frame.StagePostFrameTimer = null;
                                                              SaveReplayToFile(slot, frame);

                                                              return TimerAction.Stop;
                                                          },
                                                          timer_replay_postrun_time.GetFloat(),
                                                          GameTimerFlags.StopOnMapEnd);
    }

    private void OnPlayerRecordSaved(SteamID        playerSteamId,
                                     EAttemptResult recordType,
                                     RunRecord      savedRecord,
                                     RunRecord?     wrRecord,
                                     RunRecord?     pbRecord)
    {
        if (_playerManager.GetPlayer(playerSteamId) is not { } player || _playerFrameData[player.Slot] is not { } frameData)
        {
            return;
        }

        var isStageRecord = savedRecord.Stage > 0;

        var runId = savedRecord.Id;

        // TODO: handle two cases
        // 1. if the record is saved but our replay for the run isnt saved yet
        // the output file should contain runId
        // 2. the record isn't saved but the replay is saved
        // rename the output file to runId, but we need to know what file it is
    }

    private async Task<bool> WriteReplayToFile(ReplayFileHeader header, string path, ReplayFrameData[] framesToWrite)
    {
        try
        {
            await using var fileStream
                = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

            await JsonSerializer.SerializeAsync(fileStream, header);
            await fileStream.WriteAsync(HeaderFrameSeparatorBytes);

            var compressionLevel = Math.Max(timer_replay_file_compression_level.GetInt32(), 1);

            await using var compressionStream = new CompressionStream(fileStream, compressionLevel);

            compressionStream.SetParameter(ZSTD_cParameter.ZSTD_c_nbWorkers,
                                           Math.Max(timer_replay_file_compression_workers.GetInt32(), ProcessorCount));

            await MemoryPackSerializer.SerializeAsync(compressionStream, framesToWrite);
        }
        catch (Exception e)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            _logger.LogError(e, "Error when trying to write temporary replay file to {p}", path);

            throw;
        }

        var shouldOverwrite = await ShouldOverwrite();

        if (shouldOverwrite)
        {
            File.Move(path, path, true);

            return true;
        }

        File.Delete(path);

        return false;

        async Task<bool> ShouldOverwrite()
        {
            string? headerString;

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var       reader = new StreamReader(stream);
                headerString = await reader.ReadLineAsync();
            }
            catch (Exception)
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(headerString))
            {
                return true;
            }

            try
            {
                var originalHeader = JsonSerializer.Deserialize<ReplayFileHeader>(headerString);

                if (originalHeader == null)
                {
                    return true;
                }

                return header.Time <= originalHeader.Time;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Failed to deserialize header from existing replay {path}. Overwriting.", path);

                return true;
            }
        }
    }

    private void SaveReplayToFile(PlayerSlot slot, PlayerFrameData frame)
    {
        if (frame.IsSavingReplay)
        {
            _logger.LogWarning("Trying to call SaveReplayToFile when client {name} is already in the process of saving???",
                               frame.Name);

            return;
        }

        frame.IsSavingReplay = true;
        var style = frame.Style;
        var track = frame.Track;

        var path = Path.Combine(_replayDirectory,
                                $"style_{frame.Style}",
                                $"{_bridge.GlobalVars.MapName}_{frame.Track}.replay.{Guid.NewGuid()}");

        Task.Run(async () =>
        {
            var framesToWrite = frame.Frames.ToArray();

            var header = new ReplayFileHeader
            {
                SteamId     = frame.SteamId,
                TotalFrames = framesToWrite.Length,
                PreFrame    = frame.TimerStartFrame,
                PostFrame   = frame.TimerFinishFrame,
                Time        = frame.FinishTime,
                StageTicks  = frame.NewStageTicks,
                PlayerName  = frame.Name,
            };

            try
            {
                if (await WriteReplayToFile(header, path, framesToWrite))
                {
                    await StartNewReplay(header, framesToWrite, style, track);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to save replay at {p}", path);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_bridge.EntityManager.FindPlayerControllerBySlot(slot) is { } controller)
                    {
                        controller.PrintToChat($"Failed to save replay. Reason: {e.Message}");
                    }
                });
            }
            finally
            {
                frame.IsSavingReplay = false;
                ClearFrame(slot);
            }
        });
    }

    private async Task StartNewReplay(ReplayFileHeader header, ReplayFrameData[] framesToWrite, int style, int track)
    {
        var replayContent = new ReplayContent
        {
            Header = header,
            Frames = framesToWrite,
        };

        _replayCache[(style, track)] = replayContent;

        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            foreach (var bot in _replayBots.FindAll(i => (i.Style    == style || i.Style < 0)
                                                         && (i.Track == track || i.Track < 0)
                                                         && i.Config.StageBot == false))
            {
                bot.Frames = replayContent.Frames;
                bot.Header = header;
                StartReplay(bot);
            }
        });
    }

    private void SaveStageReplayToFile(PlayerFrameData frame,
                                       int             startTick,
                                       int             stageStartFrame,
                                       int             stageFinsihFrame,
                                       int             postRunFrameCount,
                                       int             stage,
                                       float           finishTime)
    {
        frame.StagePostFrameTimer = null;

        Task.Run(async () =>
        {
            var finalFrame = Math.Min(frame.Frames.Count, stageFinsihFrame + postRunFrameCount);

            var framesToWrite = frame.Frames[startTick..finalFrame].ToArray();

            var header = new ReplayFileHeader
            {
                SteamId     = frame.SteamId,
                TotalFrames = framesToWrite.Length,
                PreFrame    = stageStartFrame  - startTick,
                PostFrame   = stageFinsihFrame - startTick,
                Time        = finishTime,
                PlayerName  = frame.Name,
            };

            // timer_path/replays/style_id/stage/mapname_tracknum_stagenum.replay

            var path = Path.Combine(_replayDirectory,
                                    $"style_{frame.Style}",
                                    "stage",
                                    $"{_bridge.GlobalVars.MapName}_{frame.Track}_{stage}.replay");

            try
            {
                if (await WriteReplayToFile(header, path, framesToWrite).ConfigureAwait(false))
                {
                    _stageReplayCache[(frame.Style, frame.Track, stage)] = new ()
                    {
                        Frames = framesToWrite,
                        Header = header,
                    };

                    await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                    {
                        foreach (var bot in _replayBots.FindAll(i => (i.Style    == frame.Style || i.Style < 0)
                                                                     && (i.Track == frame.Track || i.Track < 0)
                                                                     && i.Config.StageBot
                                                                     && i.Stage == stage))
                        {
                            bot.Frames = framesToWrite;
                            bot.Header = header;
                            bot.Stage  = stage;
                            StartReplay(bot);
                        }
                    }).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when trying to save stage replay");
            }
        });
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams arg, HookReturnValue<EmptyHookReturn> hook)
    {
        var client = arg.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = arg.Pawn;

        if (!pawn.IsAlive)
        {
            return;
        }

        var slot = client.Slot;

        if (_playerFrameData[slot] is not { } frameData)
        {
            return;
        }

        var angles  = pawn.GetEyeAngles();
        var service = arg.Service;

        var frame = new ReplayFrameData
        {
            Origin         = pawn.GetAbsOrigin(),
            Angles         = new (angles.X, angles.Y),
            PressedButtons = service.KeyButtons,
            ChangedButtons = service.KeyChangedButtons,
            ScrollButtons  = service.ScrollButtons,
            MoveType       = pawn.MoveType,
            Velocity       = pawn.GetAbsVelocity(),
        };

        frameData.Frames.Add(frame);
    }
}
