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
using Cysharp.Text;
using Sharp.Shared.Definition;
using Sharp.Shared.GameEntities;
using SurfTimer.Extensions;
using SurfTimer.Managers.Player;
using SurfTimer.Managers.Request.Models;
using SurfTimer.Modules.Timer;

namespace SurfTimer.Modules;

internal interface IMessageModule
{
}

internal class MessageModule : IModule, IMessageModule
{
    private readonly InterfaceBridge _bridge;
    private readonly IRecordModule   _recordModule;

    public MessageModule(InterfaceBridge bridge, IRecordModule recordModule)
    {
        _bridge       = bridge;
        _recordModule = recordModule;
    }

    public bool Init()
    {
        _recordModule.OnPlayerWorldRecord += OnPlayerWorldRecord;
        _recordModule.OnPlayerFinish      += PlayerFinish;

        _recordModule.OnPlayerFinishStage += PlayerFinishStage;

        return true;
    }

    public void Shutdown()
    {
        _recordModule.OnPlayerFinish      -= PlayerFinish;
        _recordModule.OnPlayerWorldRecord -= OnPlayerWorldRecord;

        _recordModule.OnPlayerFinishStage -= PlayerFinishStage;
    }

    private void PlayerFinish(IGamePlayer       player,
                              IPlayerController controller,
                              IPlayerPawn       pawn,
                              ITimerInfo        timerInfo,
                              RunRecord?        pbInfo,
                              bool              newPB)
    {
        var track      = timerInfo.Track;
        var finishTime = timerInfo.Time;

        using var sb = ZString.CreateStringBuilder(true);

        if (!newPB && pbInfo != null)
        {
            var delta = finishTime - pbInfo.Time;

            sb.Append("You finished in ");
            sb.Append(ChatColor.Lime);
            sb.Append(Utils.FormatTime(finishTime, true));
            sb.Append(ChatColor.White);
            sb.Append(" (PB +");
            sb.Append(ChatColor.LightRed);
            sb.Append(Utils.FormatTime(delta, true));
            sb.Append(ChatColor.White);
            sb.Append(").");

            controller.PrintToChat(sb.ToString());

            return;
        }

        sb.Append(ChatColor.Lime);
        sb.Append(player.Name);
        sb.Append(ChatColor.White);
        sb.Append(" finished");

        if (track > 0)
        {
            sb.Append(ChatColor.Yellow);
            sb.Append(" Bonus ");
            sb.Append(track);
            sb.Append(ChatColor.White);
        }

        sb.Append(" in ");
        sb.Append(ChatColor.Lime);
        sb.Append(Utils.FormatTime(finishTime, true));
        sb.Append(ChatColor.White);

        if (pbInfo != null)
        {
            sb.Append(" (PB ");
            sb.Append(ChatColor.Green);
            sb.Append(Utils.FormatTime(finishTime - pbInfo.Time, true));
            sb.Append(ChatColor.White);
            sb.Append(')');
        }

        sb.Append('.');
        _bridge.ModSharp.PrintToChatWithPrefix(sb.ToString());
    }

    private void OnPlayerWorldRecord(IGamePlayer       player,
                                     IPlayerController controller,
                                     IPlayerPawn       pawn,
                                     ITimerInfo        timerInfo,
                                     RunRecord?        wrInfo,
                                     RunRecord?        pbInfo)
    {
        var track      = timerInfo.Track;
        var finishTime = timerInfo.Time;

        using var sb = ZString.CreateStringBuilder(true);

        sb.Append("NEW WR!!! ");

        sb.Append(ChatColor.Lime);
        sb.Append(player.Name);
        sb.Append(ChatColor.White);
        sb.Append(" finished");

        if (track > 0)
        {
            sb.Append(ChatColor.Yellow);
            sb.Append(" Bonus ");
            sb.Append(track);
            sb.Append(ChatColor.White);
        }

        sb.Append(" in ");
        sb.Append(ChatColor.Lime);
        sb.Append(Utils.FormatTime(finishTime, true));
        sb.Append(ChatColor.White);

        if (wrInfo != null)
        {
            sb.Append(" (WR -");
            sb.Append(ChatColor.Green);
            sb.Append(Utils.FormatTime(Math.Abs(finishTime - wrInfo.Time), true));
            sb.Append(ChatColor.White);

            if (pbInfo != null)
            {
                sb.Append(", PB -");
                sb.Append(ChatColor.Green);
                sb.Append(Utils.FormatTime(Math.Abs(finishTime - pbInfo.Time), true));
                sb.Append(ChatColor.White);
            }

            sb.Append(')');
        }

        sb.Append('.');

        _bridge.ModSharp.PrintToChatWithPrefix(sb.ToString());
    }

    private void PlayerFinishStage(IGamePlayer       player,
                                   IPlayerController controller,
                                   IPlayerPawn       pawn,
                                   IStageTimerInfo   timerInfo,
                                   RunRecord?        stageWRInfo,
                                   RunRecord?        stagePBInfo,
                                   bool              isWr,
                                   bool              newRecord)
    {
        using var sb = ZString.CreateStringBuilder(true);

        var track = timerInfo.Track;
        var style = timerInfo.Style;
        var stage = timerInfo.Stage;

        var hasWr               = stageWRInfo != null;
        var firstTimeCompletion = stagePBInfo == null;

        {
            var finishTime = timerInfo.Time;

            if (isWr)
            {
                sb.Append("NEW WR!!!");
            }

            sb.Append("You finished");
            sb.Append(ChatColor.Yellow);
            sb.Append(" Stage ");
            sb.Append(stage);

            if (track > 0)
            {
                sb.Append(" @ Bonus ");
                sb.Append(track);
            }

            sb.Append(ChatColor.White);
            sb.Append(" in ");
            sb.Append(ChatColor.Lime);
            sb.Append(Utils.FormatTime(finishTime, true));
            sb.Append(ChatColor.White);

            if (isWr && hasWr)
            {
                sb.Append(" (WRCP -");
                sb.Append(ChatColor.Green);
                sb.Append(Utils.FormatTime(Math.Abs(finishTime - stageWRInfo!.Time), true));
                sb.Append(ChatColor.White);

                if (!firstTimeCompletion)
                {
                    sb.Append(", PB -");
                    sb.Append(ChatColor.Green);
                    sb.Append(Utils.FormatTime(Math.Abs(finishTime - stagePBInfo!.Time), true));
                    sb.Append(ChatColor.White);
                }

                sb.Append(')');
            }
            else if (!firstTimeCompletion)
            {
                sb.Append(" (PB ");
                sb.Append(newRecord ? ChatColor.Green : ChatColor.Red);

                if (!newRecord)
                {
                    sb.Append('+');
                }

                sb.Append(Utils.FormatTime(finishTime - stagePBInfo!.Time, true));
                sb.Append(ChatColor.White);
                sb.Append(')');
            }

            sb.Append(". Sync: ");
            sb.Append(ChatColor.Lime);
            sb.AppendFormat("{0:.#}", timerInfo.Sync * 100.0f);
            sb.Append(ChatColor.White);
            sb.Append("%.");

            controller.PrintToChat(sb.ToString());
        }

        sb.Clear();

        {
            sb.Append("Stage Start: ");
            sb.Append(ChatColor.Lime);
            sb.Append((int) timerInfo.StartVelocity.Length());
            sb.Append(" u/s");
            sb.Append(ChatColor.White);

            if (hasWr || !firstTimeCompletion)
            {
                sb.Append(" (");
            }

            if (hasWr)
            {
                sb.Append("WRCP ");
                sb.Append(ChatColor.Yellow);
                var veloDelta = (timerInfo.StartVelocity - stageWRInfo!.GetStartVelocity()).Length();

                if (veloDelta > 0)
                {
                    sb.Append('+');
                }

                sb.Append((int) veloDelta);
                sb.Append(" u/s");

                if (!firstTimeCompletion)
                {
                    sb.Append(", ");
                    sb.Append(ChatColor.White);
                }
            }

            if (!firstTimeCompletion)
            {
                sb.Append("PB ");
                sb.Append(ChatColor.Yellow);
                var veloDelta = (timerInfo.StartVelocity - stagePBInfo!.GetStartVelocity()).Length();

                if (veloDelta >= 0)
                {
                    sb.Append('+');
                }

                sb.Append((int) veloDelta);
                sb.Append(" u/s");
            }

            if (hasWr || !firstTimeCompletion)
            {
                sb.Append(ChatColor.White);
                sb.Append(")");
            }

            sb.Append('.');
            controller.PrintToChat(sb.ToString());
        }

        sb.Clear();

        {
            sb.Append("Stage Average: ");
            sb.Append(ChatColor.Lime);
            sb.Append((int) timerInfo.AvgVelocity.Length());
            sb.Append(" u/s");
            sb.Append(ChatColor.White);

            if (hasWr || !firstTimeCompletion)
            {
                sb.Append(" (");
            }

            if (hasWr)
            {
                sb.Append("WRCP ");
                sb.Append(ChatColor.Yellow);
                var veloDelta = (int) (timerInfo.AvgVelocity - stageWRInfo!.GetAverageVelocity()).Length();

                if (veloDelta >= 0)
                {
                    sb.Append('+');
                }

                sb.Append(veloDelta);
                sb.Append(" u/s");

                if (!firstTimeCompletion)
                {
                    sb.Append(", ");
                    sb.Append(ChatColor.White);
                }
            }

            if (!firstTimeCompletion)
            {
                sb.Append("PB ");
                sb.Append(ChatColor.Yellow);
                var veloDelta = (int) (timerInfo.AvgVelocity - stagePBInfo!.GetAverageVelocity()).Length();

                if (veloDelta >= 0)
                {
                    sb.Append('+');
                }

                sb.Append(veloDelta);
                sb.Append(" u/s");
            }

            if (hasWr || !firstTimeCompletion)
            {
                sb.Append(ChatColor.White);
                sb.Append(")");
            }

            controller.PrintToChat(sb.ToString());
        }

        sb.Clear();

        {
            sb.Append("Total Time: ");
            var currentTime = timerInfo.Time;
            sb.Append(Utils.FormatTime(currentTime, true));

            /*var showDelta = false;

            if (_recordModule.GetWR(style, track) is { CheckpointOrStageInfo.Length: > 0 } rec)
            {
                var wrReachTime = rec.CheckpointOrStageInfo[stage - 1].Time;

                sb.Append(" (WR ");
                var delta = currentTime - wrReachTime;

                if (delta < 0)
                {
                    sb.Append(ChatColor.Green);
                }
                else
                {
                    sb.Append('+');
                    sb.Append(ChatColor.Red);
                }

                sb.Append(Utils.FormatTime(delta, true));
                sb.Append(ChatColor.White);
            }

            if (_recordModule.GetPlayerRecord(player.Slot, style, track) is { CheckpointOrStageInfo.Length: > 0 } playerPb)
            {
                var reachTime = playerPb.CheckpointOrStageInfo[stage - 1].Time;

                sb.Append(", PB ");
                var delta = currentTime - reachTime;

                if (delta < 0)
                {
                    sb.Append(ChatColor.Green);
                }
                else
                {
                    sb.Append('+');
                    sb.Append(ChatColor.Red);
                }

                sb.Append(Utils.FormatTime(delta, true));
                showDelta = true;
                sb.Append(ChatColor.White);
            }

            if (showDelta)
            {
                sb.Append(')');
            }
            */

            controller.PrintToChat(sb.ToString());
        }
    }
}
