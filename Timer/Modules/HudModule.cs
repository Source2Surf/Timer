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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using SurfTimer.Modules.Replay;
using SurfTimer.Modules.Timer;

namespace SurfTimer.Modules;

internal interface IHudModule
{
}

internal class HudModule : IModule, IHudModule
{
    private readonly InterfaceBridge _bridge;

    private readonly ITimerModule  _timerModule;
    private readonly IReplayModule _replayModule;
    private readonly IRecordModule _recordModule;

    private readonly ILogger<HudModule> _logger;

    public HudModule(InterfaceBridge    bridge,
                     ITimerModule       timerModule,
                     IReplayModule      replayModule,
                     IRecordModule      recordModule,
                     ILogger<HudModule> logger)
    {
        _bridge       = bridge;
        _timerModule  = timerModule;
        _replayModule = replayModule;
        _recordModule = recordModule;

        _logger = logger;
    }

    public bool Init()
    {
        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.ModSharp.InstallGameFrameHook(null, OnGameFramePost);

        return true;
    }

    public void Shutdown()
    {
        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);
        _bridge.ModSharp.RemoveGameFrameHook(null, OnGameFramePost);
    }

    private void OnGameFramePost(bool arg1, bool arg2, bool arg3)
    {
        var gameRules = _bridge.GameRules;

        if (!gameRules.IsWarmupPeriod)
        {
            gameRules.IsGameRestart = gameRules.RestartRoundTime < _bridge.GlobalVars.CurTime;
        }
    }

    private void OnPlayerRunCommandPost(IPlayerRunCommandHookParams param, HookReturnValue<EmptyHookReturn> ret)
    {
        var client = param.Client;

        if (client.IsFakeClient)
        {
            return;
        }

        var pawn = param.Pawn;

        if (!pawn.IsAlive)
        {
            if (pawn.GetObserverService() is not { } observer
                || observer.ObserverMode is ObserverMode.None or ObserverMode.Roaming)
            {
                return;
            }

            var observerTarget = observer.ObserverTarget;

            if (!observerTarget.IsValid()
                || _bridge.EntityManager.FindEntityByHandle(observerTarget)?.AsPlayerPawn() is not { } targetPawn
                || targetPawn.GetController() is not { } targetController)
            {
                return;
            }

            if (targetController.IsFakeClient)
            {
                if (_replayModule.GetReplayBotData(targetController.PlayerSlot) is not { } replayData)
                {
                    return;
                }

                PrintReplayHud(client, targetPawn, replayData);

                return;
            }

            if (_timerModule.GetTimerInfo(targetController.PlayerSlot) is not { } targetTimerInfo)
            {
                return;
            }

            PrintPlayerHud(client, targetController.PlayerSlot, targetPawn, targetTimerInfo);

            return;
        }

        var slot = client.Slot;

        if (_timerModule.GetTimerInfo(slot) is not { } timerInfo)
        {
            return;
        }

        PrintPlayerHud(client, slot, pawn, timerInfo);
    }

    private void PrintPlayerHud(IGameClient client, PlayerSlot slot, IPlayerPawn pawn, ITimerInfo timerInfo)
    {
        var velocity = pawn.GetAbsVelocity();

        using var sb = ZString.CreateStringBuilder(true);

        sb.AppendFormat("<span color='#00FF00'>{0}</span>", Utils.FormatTime(timerInfo.Time));

        /*
        sb.Append("&nbsp;&nbsp;");

        sb.AppendFormat("<span color='#D3D3D3'>[#{0}]</span>",
                        _recordModule.GetRankForTime(timerInfo.Style, timerInfo.Track, timerInfo.Time));
        */

        sb.Append("<br>");

        sb.AppendFormat("<span color='#FFFFFF'>{0}&nbsp;&nbsp;&nbsp;&nbsp;",
                        (int) velocity.Length2D());

        sb.AppendFormat("Sync: {0}%</span>", (timerInfo.Sync * 100).ToString("F1"));

        sb.Append("<br>");

        var pbTime = "N/A";
        var wrTime = "N/A";

        if (_recordModule.GetPlayerRecord(slot, timerInfo.Style, timerInfo.Track) is { } pb)
        {
            pbTime = Utils.FormatTime(pb.Time, true);
        }

        if (_recordModule.GetWRTime(timerInfo.Style, timerInfo.Track) is { } wr)
        {
            wrTime = Utils.FormatTime(wr, true);
        }

        sb.AppendFormat("<span color='#808080'>PB: {0} ‖ WR: {1}</span>",
                        pbTime,
                        wrTime,
                        true);

        PrintHtmlToPlayer(client, sb.ToString());
    }

    private void PrintReplayHud(IGameClient client, IPlayerPawn pawn, ReplayBotData bot)
    {
        if (bot.Status == EReplayBotStatus.Idle)
        {
            PrintHtmlToPlayer(client,
                              $"<span class='fontSize-xl' color='{GetRainbowHex(_bridge.GlobalVars.CurTime)}'>IDLE</span>");

            return;
        }

        using var    sb              = ZString.CreateStringBuilder(true);
        const string colorLabel      = "#AAAAAA";
        const string colorData       = "#E0E0E0";
        const string colorStageBot   = "#2196F3";
        const string colorFullRunBot = "#FFD700";
        const string colorSubtleInfo = "#B0B0B0";

        if (bot.Stage > 0)
        {
            sb.AppendFormat("<span color='{0}'>Stage {1} Replay Bot</span>", colorStageBot, bot.Stage);
        }
        else
        {
            sb.AppendFormat("<span color='{0}'>Replay Bot</span>", colorFullRunBot);

            var currentStage = bot.GetCurrentStage();

            if (currentStage > 0)
            {
                sb.AppendFormat(" <span color='{0}'>(Stage {1})</span>", colorSubtleInfo, currentStage);
            }
        }

        sb.Append("<br>");

        var header = bot.Header!;

        sb.AppendFormat("<span color='{0}'>Player:</span> <span color='{1}'>{2}</span>",
                        colorLabel,
                        colorData,
                        header.PlayerName);

        sb.Append("<br>");

        sb.AppendFormat("<span color='{0}'>Time: </span>", colorLabel);
        var timedFrame = Math.Clamp(bot.CurrentFrame, header.PreFrame, header.PostFrame);

        sb.AppendFormat("<span color='{0}'>{1}/{2}</span>",
                        colorData,
                        Utils.FormatTime(Utils.TickInterval * (timedFrame - header.PreFrame)),
                        Utils.FormatTime(bot.Time));

        sb.Append("<br>");

        sb.AppendFormat("<span color='{0}'>Speed:</span> <span color='{1}'>{2}</span>",
                        colorLabel,
                        colorData,
                        (int) MathF.Round(pawn.GetAbsVelocity().Length2D()));

        PrintHtmlToPlayer(client, sb.ToString());
    }

    private static string GetRainbowHex(float curtime)
    {
        const float frequency = 3.5f;
        const float amplitude = 127f;
        const float center    = 128f;
        const float twoPi     = MathF.PI * 2f;

        var red   = (MathF.Sin((frequency * curtime) + 0f)                  * amplitude) + center;
        var green = (MathF.Sin((frequency * curtime) + (twoPi        / 3f)) * amplitude) + center;
        var blue  = (MathF.Sin((frequency * curtime) + ((twoPi * 2f) / 3f)) * amplitude) + center;

        var r = (int) Math.Clamp(red,   0f, 255f);
        var g = (int) Math.Clamp(green, 0f, 255f);
        var b = (int) Math.Clamp(blue,  0f, 255f);

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private void PrintHtmlToPlayer(IGameClient client, string html)
    {
        if (_bridge.EventManager.CreateEvent("show_survival_respawn_status", true) is not { } e)
        {
            return;
        }

        e.SetString("loc_token", html);
        e.SetInt("duration", 1);
        e.SetInt("userid",   client.UserId);
        e.FireToClient(client);
    }
}
