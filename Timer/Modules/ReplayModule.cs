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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using SurfTimer.Extensions;
using SurfTimer.Managers;
using SurfTimer.Managers.Player;
using SurfTimer.Modules.Replay;
using ZLinq;

namespace SurfTimer.Modules;

internal interface IReplayModule
{
    ReplayBotData? GetReplayBotData(PlayerSlot slot);
}

internal partial class ReplayModule : IReplayModule, IModule, IGameListener, IEntityListener
{
    private const           char   HeaderFrameSeparator      = '\n';
    private static readonly byte[] HeaderFrameSeparatorBytes = [(byte) HeaderFrameSeparator];

    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 0;

    private readonly InterfaceBridge _bridge;
    private readonly ICommandManager _commandManager;

    private readonly ILogger<ReplayModule> _logger;

    private readonly PlayerFrameData?[] _playerFrameData;

    private readonly IPlayerManager _playerManager;
    private readonly IRecordModule  _recordModule;
    private readonly ITimerModule   _timerModule;
    private readonly IStyleModule   _styleModule;

    private readonly Dictionary<(int style, int track), ReplayContent>            _replayCache      = [];
    private readonly Dictionary<(int style, int track, int stage), ReplayContent> _stageReplayCache = [];

    private readonly List<ReplayBotData> _replayBots       = [];
    private          ReplayBotConfig[]   _replayBotConfigs = [];

    private static          bool _expectingBot;
    private static readonly int  ProcessorCount = Environment.ProcessorCount;

    private readonly string _replayDirectory;
    private readonly string _replayConfigPath;

    // ReSharper disable InconsistentNaming
    private static unsafe delegate* unmanaged<nint, int, int, nint, CStrikeWeaponType, int, bool>
        CCSBotManager_BotAddCommand;

    private readonly IConVar timer_replay_delay;
    private readonly IConVar timer_replay_prerun_time;
    private readonly IConVar timer_replay_postrun_time;
    private readonly IConVar timer_replay_stage_prerun_time;
    private readonly IConVar timer_replay_stage_postrun_time;
    private readonly IConVar timer_replay_file_compression_level;
    private readonly IConVar timer_replay_file_compression_workers;

    // ReSharper restore InconsistentNaming

    public ReplayModule(InterfaceBridge       bridge,
                        IPlayerManager        playerManager,
                        ICommandManager       commandManager,
                        ITimerModule          timerModule,
                        IRecordModule         recordModule,
                        IStyleModule          styleModule,
                        IGameData             gameData,
                        IInlineHookManager    inlineHookManager,
                        ILogger<ReplayModule> logger)
    {
        _bridge         = bridge;
        _playerManager  = playerManager;
        _commandManager = commandManager;
        _timerModule    = timerModule;
        _recordModule   = recordModule;
        _styleModule    = styleModule;
        _logger         = logger;

        timer_replay_delay = bridge.ConVarManager.CreateConVar("timer_replay_delay", 2.0f, 0.0f, 5.0f, "回放结束后要等多久才开始放")!;

        timer_replay_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_prerun_time", 2.0f, 2.0f, 10.0f, "记录玩家在离开起点前x秒的数据")!;

        timer_replay_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_postrun_time", 2.0f, 2.0f, 10.0f, "记录玩家在完成后x秒的数据")!;

        timer_replay_stage_prerun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_prerun_time", 2.0f, 0.0f, 10.0f, "记录玩家在离开关卡起点前x秒的数据")!;

        timer_replay_stage_postrun_time
            = bridge.ConVarManager.CreateConVar("timer_replay_stage_postrun_time", 2.0f, 0.0f, 10.0f, "记录玩家在完成关卡后x秒的数据")!;

        timer_replay_file_compression_level
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_level", 3, 0, 19, "回放文件的压缩等级，0为关闭压缩")!;

        timer_replay_file_compression_workers
            = bridge.ConVarManager.CreateConVar("timer_replay_file_compression_workers", 4, 0, 256, "压缩回放文件时用多少个线程,0为关闭")!;

        unsafe
        {
            if (!inlineHookManager.AddHook("CCSBotManager::MaintainBotQuota",
                                           (nint) (delegate* unmanaged<nint, void>) (&hk_CCSBotManager_MaintainBotQuota),
                                           out _))

            {
                throw new InvalidOperationException("Failed to hook CCSBotManager::MaintainBotQuota");
            }

            CCSBotManager_BotAddCommand
                = (delegate* unmanaged<nint, int, int, nint, CStrikeWeaponType, int, bool>)
                gameData.GetAddress("CCSBotManager::BotAddCommand");
        }

        _playerFrameData = Enumerable.Repeat<PlayerFrameData?>(null, PlayerSlot.MaxPlayerSlot).ToArray();

        _replayDirectory  = Path.Combine(bridge.TimerDataPath, "replays");
        _replayConfigPath = Path.Combine(bridge.TimerDataPath, "replay.json");

        LoadRepalyBotConfigs();

        if (!Directory.Exists(_replayDirectory))
        {
            Directory.CreateDirectory(_replayDirectory);
        }

        // path/data/surftimer/replays/style_id/mapname_tracknum.replay
        // path/data/surftimer/replays/style_id/stage/mapname_tracknum_stagenum.replay
        for (var i = 0; i < Utils.MAX_STYLE; i++)
        {
            var stylePath = Path.Combine(_replayDirectory, $"style_{i}");

            if (!Directory.Exists(stylePath))
            {
                Directory.CreateDirectory(stylePath);
            }

            var stagePath = Path.Combine(stylePath, "stage");

            if (!Directory.Exists(stagePath))
            {
                Directory.CreateDirectory(stagePath);
            }
        }
    }

    public void OnServerActivate()
    {
        // ReSharper disable InconsistentNaming
        if (_bridge.ConVarManager.FindConVar("bot_zombie") is { } bot_zombie)
        {
            bot_zombie.Flags &= ~ConVarFlags.Cheat;
            bot_zombie.Set("1");
        }

        if (_bridge.ConVarManager.FindConVar("bot_stop") is { } bot_stop)
        {
            bot_stop.Flags &= ~ConVarFlags.Cheat;
            bot_stop.Set("1");
        }

        // ReSharper restore InconsistentNaming

        Task.Run(() =>
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            for (var i = 0; i < Utils.MAX_TRACK; i++)
            {
                for (var j = 0; j < Utils.MAX_STYLE; j++)
                {
                    for (var k = 0; k < Utils.MAX_STAGE; k++)
                    {
                        LoadReplay(j, i, k);
                    }
                }
            }

            stopwatch.Stop();

            _logger.LogInformation("LoadReplay: {stop}", stopwatch.Elapsed);
        });

        _bridge.ModSharp.RepeatCallThisMap(3.0f, Timer_CheckReplayBot);
    }

    private unsafe void AddReplayBot()
    {
        for (var i = 0; i < _replayBotConfigs.Length; i++)
        {
            _expectingBot = true;

            if (!CCSBotManager_BotAddCommand(0, Random.Shared.Next(2, 4), 0, 0, CStrikeWeaponType.Unknown, 0))
            {
                _logger.LogError("Failed to add bot");
            }

            _expectingBot = false;
        }
    }

    public void OnGameShutdown()
    {
        _replayBots.Clear();
        _replayCache.Clear();
        _stageReplayCache.Clear();

        for (var i = 0; i < PlayerSlot.MaxPlayerSlot; i++)
        {
            _playerFrameData[i] = null;
        }
    }

    public bool Init()
    {
        _bridge.ModSharp.InstallGameListener(this);

        _bridge.HookManager.PlayerRunCommand.InstallHookPost(OnPlayerRunCommandPost);

        _bridge.HookManager.PlayerProcessMovePre.InstallForward(OnPlayerProcessMovementPre);
        _bridge.HookManager.PlayerProcessMovePost.InstallForward(OnPlayerProcessMovementPost);

        _playerManager.ClientPutInServer  += OnClientPutInServer;
        _playerManager.ClientDisconnected += OnClientDisconnected;

        _timerModule.OnPlayerTimerStart += OnTimerStart;
        _timerModule.OnPlayerFinishMap  += OnPlayerFinishMap;

        _timerModule.OnPlayerStageTimerStart  += OnPlayerStageTimerStart;
        _timerModule.OnPlayerStageTimerFinish += OnPlayerStageTimerFinish;

        _recordModule.OnPlayerRecordSaved      += OnPlayerRecordSaved;
        _recordModule.OnPlayerStageRecordSaved += OnPlayerRecordSaved;

        /*_recordModule.OnPlayerWorldRecord     += OnPlayerWorldRecord;*/

        return true;
    }

    public void Shutdown()
    {
        _bridge.ModSharp.RemoveGameListener(this);

        _bridge.HookManager.PlayerRunCommand.RemoveHookPost(OnPlayerRunCommandPost);

        _bridge.HookManager.PlayerProcessMovePre.RemoveForward(OnPlayerProcessMovementPre);
        _bridge.HookManager.PlayerProcessMovePost.RemoveForward(OnPlayerProcessMovementPost);

        _playerManager.ClientPutInServer  -= OnClientPutInServer;
        _playerManager.ClientDisconnected -= OnClientDisconnected;

        _timerModule.OnPlayerTimerStart -= OnTimerStart;
        _timerModule.OnPlayerFinishMap  -= OnPlayerFinishMap;

        _timerModule.OnPlayerStageTimerStart  -= OnPlayerStageTimerStart;
        _timerModule.OnPlayerStageTimerFinish -= OnPlayerStageTimerFinish;

        /*_recordModule.OnPlayerWorldRecord     -= OnPlayerWorldRecord;*/
    }

    private void OnClientPutInServer(IGamePlayer player)
    {
        var slot = player.Slot;

        if (!player.IsFakeClient)
        {
            var data = new PlayerFrameData
            {
                Frames = new ()
                {
                    Capacity = Utils.Tickrate * 60 * 5,
                },
                SteamId = player.SteamId,
                Name    = player.Name,
            };

            _playerFrameData[slot] = data;

            return;
        }

        if (!_expectingBot)
        {
            _bridge.ClientManager.KickClient(player.Client,
                                             "no",
                                             NetworkDisconnectionReason.Kicked);

            return;
        }

        if (_bridge.EntityManager.FindPlayerControllerBySlot(player.Client.Slot) is not { IsValidEntity: true } controller)
        {
            _logger.LogError("Failed to find bot!!!!");

            _bridge.ClientManager.KickClient(player.Client,
                                             "no",
                                             NetworkDisconnectionReason.Kicked);

            return;
        }

        var botData = new ReplayBotData
        {
            Controller   = controller,
            Index        = controller.Index,
            Frames       = [],
            CurrentFrame = 0,
            Client       = player.Client,
            Status       = EReplayBotStatus.Idle,
            Type         = EReplayBotType.Looping,
            Config       = _replayBotConfigs[_replayBots.Count],
        };

        _replayBots.Add(botData);

        if (!botData.Config.StageBot)
        {
            FindNextReplay(botData);
        }
        else
        {
            FindNextStageReplay(botData);
        }

        StartReplay(botData);
    }

    private void OnClientDisconnected(IGamePlayer player)
    {
        var slot = player.Slot;

        if (player.IsFakeClient)
        {
            if (_replayBots.Find(i => i.Client.Equals(player.Client)) is { } bot)
            {
                _replayBots.Remove(bot);
            }

            return;
        }

        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        if (frame.StagePostFrameTimer is { } stagePostFrameTimer)
        {
            _bridge.ModSharp.StopTimer(stagePostFrameTimer);
            frame.StagePostFrameTimer = null;
        }

        if (frame.GrabbingPostFrame)
        {
            frame.GrabbingPostFrame = false;
            SaveReplayToFile(slot, frame);

            if (frame.PostFrameTimer is { } timer)
            {
                _bridge.ModSharp.StopTimer(timer);
            }

            frame.PostFrameTimer = null;
        }
    }

    private void ClearFrame(PlayerSlot slot)
    {
        if (_playerFrameData[slot] is not { } frame)
        {
            return;
        }

        frame.TimerStartFrame  = 0;
        frame.TimerFinishFrame = 0;
        frame.FinishTime       = 0;
        frame.Frames.Clear();
        frame.Frames.Capacity = Utils.Tickrate * 60 * 5;
    }

    private void ClearReplayBots()
    {
        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            foreach (var botData in _replayBots)
            {
                _bridge.ClientManager.KickClient(botData.Client, "byeee", NetworkDisconnectionReason.Kicked);
            }

            _replayBots.Clear();
        });
    }

    private void LoadRepalyBotConfigs()
    {
        _replayBotConfigs = [new ()];

        if (!File.Exists(_replayConfigPath))
        {
            File.WriteAllText(_replayConfigPath, JsonSerializer.Serialize(_replayBotConfigs, Utils.SerializerOptions));

            _logger.LogWarning("Failed to find replay config at {path}, generating the default one...", _replayConfigPath);

            return;
        }

        try
        {
            var configs
                = JsonSerializer.Deserialize<ReplayBotConfig[]>(File.ReadAllText(_replayConfigPath),
                                                                Utils.DeserializerOptions);

            if (configs == null || configs.Length == 0)
            {
                _logger.LogWarning("Failed to deserialize replay config, regenerate with default config");
                File.WriteAllText(_replayConfigPath, JsonSerializer.Serialize(_replayBotConfigs, Utils.SerializerOptions));
            }
            else
            {
                _replayBotConfigs = configs;
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to deserialize replay config, regenerate with default config");
            File.WriteAllText(_replayConfigPath, JsonSerializer.Serialize(_replayBotConfigs, Utils.SerializerOptions));
        }
    }

    public ReplayBotData? GetReplayBotData(PlayerSlot slot)
    {
        return _replayBots.AsValueEnumerable().FirstOrDefault(bot => bot.Client.Slot == slot);
    }
}
