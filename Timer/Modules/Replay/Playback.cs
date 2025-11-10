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
using System.Runtime.InteropServices;
using System.Text.Json;
using MemoryPack;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using SurfTimer.Modules.Replay;
using ZstdSharp;

// ReSharper disable once CheckNamespace
namespace SurfTimer.Modules;

internal partial class ReplayModule
{
    [UnmanagedCallersOnly]
    private static void hk_CCSBotManager_MaintainBotQuota(nint a1)
    {
    }

    private void Timer_CheckReplayBot()
    {
        if (_replayBots.Count == 0)
        {
            AddReplayBot();

            return;
        }

        foreach (var bot in _replayBots)
        {
            if (_bridge.EntityManager.FindPlayerControllerBySlot(bot.Client.Slot) is not { IsValidEntity: true } controller)
            {
                continue;
            }

            if (controller.GetPlayerPawn() is not { IsValidEntity: true } pawn)
            {
                continue;
            }

            if (!pawn.IsAlive)
            {
                controller.Respawn();

                continue;
            }

            pawn.RemoveAllItems();
        }
    }

    private void StartReplay(ReplayBotData bot)
    {
        bot.CurrentFrame = 0;

        var header = bot.Header;

        if (header == null || bot.Frames.Length == 0)
        {
            bot.Status = EReplayBotStatus.Idle;
        }
        else
        {
            bot.Status = EReplayBotStatus.Start;
            bot.Time   = header.Time;

            bot.Timer = _bridge.ModSharp.PushTimer(() =>
                                                   {
                                                       bot.Timer  = null;
                                                       bot.Status = EReplayBotStatus.Running;

                                                       return TimerAction.Stop;
                                                   },
                                                   timer_replay_delay.GetFloat(),
                                                   GameTimerFlags.StopOnMapEnd);
        }

        SetupReplayBotName(bot);
    }

    private void SetupReplayBotName(ReplayBotData bot)
    {
        var config = bot.Config;

        var name = bot.Status == EReplayBotStatus.Idle ? config.IdleName : config.Name;

        var trackStr   = string.Empty;
        var stageStr   = string.Empty;
        var timeStr    = string.Empty;
        var styleStr   = string.Empty;

        if (bot.Status == EReplayBotStatus.Idle)
        {
            trackStr = config.PlayType switch
            {
                EReplayBotPlayType.All       => Utils.GetTrackName(Math.Max(0, bot.Track), true),
                EReplayBotPlayType.MainOnly  => "Main",
                EReplayBotPlayType.BonusOnly => "Bonus",
                _                            => trackStr,
            };
        }
        else
        {
            trackStr = Utils.GetTrackName(bot.Track);

            if (config.StageBot)
            {
                stageStr = $" Stage {bot.Stage}";
            }

            timeStr  = Utils.FormatTime(bot.Header!.Time, true);
            styleStr = _styleModule.GetStyleSetting(bot.Style).Name;
        }

        name = name.Replace("{track}", trackStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{stage}", stageStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{style}", styleStr, StringComparison.OrdinalIgnoreCase)
                   .Replace("{time}",  timeStr,  StringComparison.OrdinalIgnoreCase);

        bot.Client.SetName(name);

        unsafe
        {
            *(bool*) (bot.Client.GetAbsPtr() + 0x138) = true;
        }
    }

    private void FindNextReplay(ReplayBotData bot)
    {
        var       maxStyle = _styleModule.GetStyleCount();
        const int maxTrack = Utils.MAX_TRACK;
        var       total    = maxStyle * maxTrack;

        var config = bot.Config;

        var startIndex = bot.Track < 0
            ? 0
            : (bot.Track * maxStyle) + bot.Style + 1;

        // scan the next total–1 entries
        for (var step = 0; step < total; step++)
        {
            var idx = (startIndex + step) % total;

            var track = idx / maxStyle;

            if (!bot.IsTrackAllowed(track))
            {
                continue;
            }

            var style = idx % maxStyle;

            if (!config.Styles.Contains(style))
            {
                continue;
            }

            if (_replayCache.TryGetValue((style, track), out var content))
            {
                bot.Header = content.Header;
                bot.Frames = content.Frames;
                bot.Style  = style;
                bot.Time   = content.Header.Time;
                bot.Track  = track;

                return;
            }
        }

        if (bot.Track < 0)
        {
            bot.Track = 0;
            bot.Style = 0;
        }
    }

    private void FindNextStageReplay(ReplayBotData bot)
    {
        var       maxStyle = _styleModule.GetStyleCount();
        const int maxTrack = Utils.MAX_TRACK;
        const int maxStage = Utils.MAX_STAGE;

        var total = maxStage * maxTrack * maxStyle;

        int startIndex;

        if (bot.Track < 0)
        {
            startIndex = 0;
        }
        else
        {
            startIndex = (bot.Stage   * maxTrack * maxStyle)
                         + (bot.Track * maxStyle)
                         + bot.Style;
        }

        var config = bot.Config;

        // scan the next total–1 entries
        for (var step = 0; step < total; step++)
        {
            var idx = (startIndex + step) % total;

            var stage = idx / (maxTrack * maxStyle);

            var rem = idx % (maxTrack * maxStyle);

            var track = rem / maxStyle;

            if (!bot.IsTrackAllowed(track))
            {
                continue;
            }

            var style = rem % maxStyle;

            if (!config.Styles.Contains(style))
            {
                continue;
            }

            if (_stageReplayCache.TryGetValue((style, track, stage), out var content))
            {
                bot.Header = content.Header;
                bot.Frames = content.Frames;
                bot.Style  = style;
                bot.Track  = track;
                bot.Time   = content.Header.Time;
                bot.Stage  = stage;

                return;
            }
        }

        if (bot.Track < 0)
        {
            bot.Track = 0;
            bot.Style = 0;
        }
    }

    private void LoadReplay(int style, int track, int stage = 0)
    {
        var isStageReplay = stage > 0;

        var path = isStageReplay
            ? Path.Combine(_replayDirectory,
                           $"style_{style}",
                           "stage",
                           $"{_bridge.GlobalVars.MapName}_{track}_{stage}.replay")
            : Path.Combine(_replayDirectory,
                           $"style_{style}",
                           $"{_bridge.GlobalVars.MapName}_{track}.replay");

        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var bytes = File.ReadAllBytes(path).AsSpan();

            var split = bytes.IndexOf((byte) HeaderFrameSeparator);

            if (split == -1)
            {
                _logger.LogError("Invalid replay file found at {path}, removing", path);
                File.Delete(path);

                return;
            }

            var header = JsonSerializer.Deserialize<ReplayFileHeader>(bytes[..split]);

            if (header == null)
            {
                _logger.LogError("Failed to deserialize replay header. Path: {p}", path);

                return;
            }

            var compressedFrameBytes = bytes[(split + 1)..];

            var decompressor = new Decompressor();

            var decompressedBuffer = decompressor.Unwrap(compressedFrameBytes);

            if (MemoryPackSerializer.Deserialize<ReplayFrameData[]>(decompressedBuffer) is not { } frameData)
            {
                _logger.LogError("Failed to deserialize frame data. Path: {p}", path);

                return;
            }

            var content = new ReplayContent
            {
                Header = header,
                Frames = frameData,
            };

            if (isStageReplay)
            {
                _stageReplayCache[(style, track, stage)] = content;
            }
            else
            {
                _replayCache[(style, track)] = content;
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error when trying to load replay at path: {p}", path);
        }
    }

    private void OnPlayerProcessMovementPre(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsFakeClient)
        {
            return;
        }

        if (_replayBots.Find(i => i.Client.Equals(client)) is not { } bot)
        {
            return;
        }

        if (bot.Status != EReplayBotStatus.Running)
        {
            return;
        }

        if (bot.CurrentFrame >= bot.Frames.Length)
        {
            return;
        }

        var frame = bot.Frames[bot.CurrentFrame];

        var service = arg.Service;

        service.KeyButtons        = frame.PressedButtons;
        service.KeyChangedButtons = frame.ChangedButtons;
        service.ScrollButtons     = frame.ScrollButtons;
    }

    private unsafe void OnPlayerProcessMovementPost(IPlayerProcessMoveForwardParams arg)
    {
        var client = arg.Client;

        if (!client.IsFakeClient)
        {
            return;
        }

        if (_replayBots.Find(i => i.Client.Equals(client)) is not { } bot)
        {
            return;
        }

        var totalFrames = bot.Frames.Length;

        var pawn = arg.Pawn;

        if (bot.Status == EReplayBotStatus.Idle)
        {
            pawn.SetMoveType(MoveType.None);

            return;
        }

        if (bot.CurrentFrame >= totalFrames && bot.Status != EReplayBotStatus.End)
        {
            bot.Status = EReplayBotStatus.End;

            bot.Timer = _bridge.ModSharp.PushTimer(() =>
                                                   {
                                                       bot.Timer = null;

                                                       if (bot.Type == EReplayBotType.Looping)
                                                       {
                                                           if (bot.Config.StageBot)
                                                           {
                                                               FindNextStageReplay(bot);
                                                           }
                                                           else
                                                           {
                                                               FindNextReplay(bot);
                                                           }

                                                           StartReplay(bot);
                                                       }
                                                       else
                                                       {
                                                           bot.Controller.Respawn();
                                                       }

                                                       return TimerAction.Stop;
                                                   },
                                                   timer_replay_delay.GetFloat() / 2.0f,
                                                   GameTimerFlags.StopOnMapEnd);
        }

        var ending = bot.Status == EReplayBotStatus.End;

        var curFrame = ending ? totalFrames - 1 : bot.CurrentFrame;

        var mv = arg.Info;

        var frame  = bot.Frames[curFrame];
        var angles = frame.Angles;
        mv->ViewAngles = new (angles.X, angles.Y, 0);
        mv->Angles     = mv->ViewAngles;

        var curPos = frame.Origin;
        mv->AbsOrigin = curPos;

        var flags = pawn.Flags;

        flags |= EntityFlags.AtControls;
        flags &= ~(EntityFlags.OnGround | EntityFlags.Fly | EntityFlags.WaterJump);

        pawn.Flags = flags;

        pawn.SetMoveType(bot.Status == EReplayBotStatus.Running ? frame.MoveType : MoveType.None);

        mv->AbsOrigin = curPos;
        mv->Velocity  = frame.Velocity;

        if (bot.Status == EReplayBotStatus.Running)
        {
            bot.CurrentFrame++;
        }
    }
}
