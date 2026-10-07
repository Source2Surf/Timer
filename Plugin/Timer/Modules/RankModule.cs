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
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Cysharp.Text;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Managers.Player;
using Source2Surf.Timer.Modules.Rank;
using Source2Surf.Timer.Shared.Events;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Utilities;

namespace Source2Surf.Timer.Modules;

internal interface IRankModule
{
}

/// <summary>
///     Global points ranks of the players on the server: titles by rank (timer-ranks.jsonc) as a chat tag and the
///     scoreboard clan tag, and !ptop.
/// </summary>
internal class RankModule : IModule, IRankModule, IClientListener, IPlayerManagerListener, IRecordModuleListener
{
    private const double RefreshInterval  = 120; // ranks also move with runs on other servers
    private const double AfterJoinDelay   = 1;
    private const double AfterRecordDelay = 10;  // the backend recalculates points shortly after a run
    private const int    TopPlayers       = 10;

    private readonly InterfaceBridge       _bridge;
    private readonly IRequestManager       _request;
    private readonly ICommandManager       _commandManager;
    private readonly IPlayerManager        _playerManager;
    private readonly IRecordModule         _recordModule;
    private readonly ILocalizationProvider _localization;
    private readonly ILogger<RankModule>   _logger;

    private readonly RankConfig _config;
    private readonly RankTitles _titles;

    private readonly int[]     _ranks       = new int[PlayerSlot.MaxPlayerCount];
    private readonly uint[]    _points      = new uint[PlayerSlot.MaxPlayerCount];
    private readonly string?[] _appliedTags = new string?[PlayerSlot.MaxPlayerCount];
    private          int       _total;
    private          bool      _refreshing;
    private          bool      _refreshAgain;
    private          float     _refreshDueAt = float.PositiveInfinity; // the next scheduled refresh; none at infinity

    // Host_Say sends a chat line to each recipient in turn, ~64 copies in one tick: it's rendered once.
    private (int Tick, int Index, string Token, string? Name, string? Text, string Line)? _lastChat;
    private          bool      _shutDown;
    private          Guid      _refreshTimer;

    public RankModule(InterfaceBridge       bridge,
                      IRequestManager       request,
                      ICommandManager       commandManager,
                      IPlayerManager        playerManager,
                      IRecordModule         recordModule,
                      ILocalizationProvider localization,
                      ILogger<RankModule>   logger)
    {
        _bridge         = bridge;
        _request        = request;
        _commandManager = commandManager;
        _playerManager  = playerManager;
        _recordModule   = recordModule;
        _localization   = localization;
        _logger         = logger;

        var configDir = Path.Combine(bridge.SharpPath, "configs");
        Directory.CreateDirectory(configDir);
        _config = RankConfig.Load(Path.Combine(configDir, "timer-ranks.jsonc"), logger);
        _titles = new RankTitles(_config);
    }

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);
        _playerManager.RegisterListener(this);
        _recordModule.RegisterListener(this);
        _bridge.HookManager.PlayerSpawnPost.InstallForward(OnPlayerSpawnPost);

        // Never unhooked: the hooked ids are shared by every module, not counted.
        _bridge.ModSharp.HookNetMessage(ProtobufNetMessageType.UM_SayText2);
        _bridge.HookManager.PostEventAbstract.InstallHookPre(OnPostEventPre);

        _commandManager.AddClientChatCommand("ptop", OnCommandTop);

        _refreshTimer = _bridge.ModSharp.PushTimer(Refresh, RefreshInterval, GameTimerFlags.Repeatable);

        return true;
    }

    public void Shutdown()
    {
        // Refreshes already scheduled still fire after a reload.
        _shutDown = true;
        _bridge.ModSharp.StopTimer(_refreshTimer);
        _bridge.HookManager.PostEventAbstract.RemoveHookPre(OnPostEventPre);
        _bridge.HookManager.PlayerSpawnPost.RemoveForward(OnPlayerSpawnPost);
        _recordModule.UnregisterListener(this);
        _playerManager.UnregisterListener(this);
        _bridge.ClientManager.RemoveClientListener(this);
    }

    int IClientListener.ListenerVersion  => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;

    private RankTitle? TitleOf(PlayerSlot slot)
        => _titles.For(_ranks[slot], _total);

    // ------------------------------------------------------------------ ranks

    public void OnClientPutInServer(PlayerSlot slot)
    {
        _ranks[slot]       = 0;
        _points[slot]      = 0;
        _appliedTags[slot] = null;
        RefreshAfter(AfterJoinDelay);
    }

    public void OnClientDisconnected(PlayerSlot slot)
    {
        _ranks[slot]       = 0;
        _points[slot]      = 0;
        _appliedTags[slot] = null;
    }

    public void OnClientInfoLoaded(SteamID steamId)
        => RefreshAfter(AfterJoinDelay);

    public void OnRecordSaved(PlayerRecordSavedEvent recordEvent)
    {
        if (recordEvent.RecordType is EAttemptResult.NewPersonalRecord or EAttemptResult.NewServerRecord)
        {
            RefreshAfter(AfterRecordDelay);
        }
    }

    // Joins and records ask for one: a refresh already due as soon covers them, so a burst makes one request.
    private void RefreshAfter(double seconds)
    {
        var now = _bridge.GlobalVars.CurTime;
        var due = now + (float) seconds;

        // One due already passed never ran (a timer lost with the map): it doesn't count.
        if (due >= _refreshDueAt && _refreshDueAt > now)
        {
            return;
        }

        _refreshDueAt = due;
        _bridge.ModSharp.PushTimer(RefreshWhenDue, seconds);
    }

    // A timer outrun by an earlier one finds its refresh done.
    private void RefreshWhenDue()
    {
        if (_bridge.GlobalVars.CurTime + 0.01f < _refreshDueAt)
        {
            return;
        }

        _refreshDueAt = float.PositiveInfinity;
        Refresh();
    }

    // Everyone on the server in one request; a refresh asked for meanwhile runs right after.
    private void Refresh()
    {
        if (_shutDown)
        {
            return;
        }

        if (_refreshing)
        {
            _refreshAgain = true;

            return;
        }

        var players = new List<(PlayerSlot Slot, SteamID SteamId)>();

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (!client.IsFakeClient && !client.IsHltv && (ulong) client.SteamId != 0)
            {
                players.Add((client.Slot, client.SteamId));
            }
        }

        if (players.Count == 0)
        {
            return;
        }

        _refreshing = true;
        _           = RefreshAsync(players);
    }

    private async Task RefreshAsync(List<(PlayerSlot Slot, SteamID SteamId)> players)
    {
        IReadOnlyDictionary<SteamID, (int Rank, uint Points)>? ranked = null;
        var                                                    total  = 0;

        try
        {
            (ranked, total) = await _request.GetPlayersPointsRank(players.ConvertAll(p => p.SteamId)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to load the players' points ranks");
        }

        await _bridge.ModSharp.InvokeFrameActionAsync(() =>
        {
            _refreshing = false;

            if (ranked is not null && !_shutDown)
            {
                _total = total;

                foreach (var (slot, steamId) in players)
                {
                    // The slot may have changed hands while the request ran.
                    if (_bridge.ClientManager.GetGameClient(slot) is { } client && client.SteamId == steamId)
                    {
                        (_ranks[slot], _points[slot]) = ranked.GetValueOrDefault(steamId);
                        ApplyClanTag(slot);
                        ApplyScore(slot);
                    }
                }
            }

            if (_refreshAgain)
            {
                _refreshAgain = false;
                Refresh();
            }
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ scoreboard

    // The game sets the clan tag again from the player's settings, so it's put back after those and on spawning.
    public void OnClientSettingChanged(IGameClient client)
    {
        _appliedTags[client.Slot] = null;
        ApplyClanTag(client.Slot);
    }

    private void OnPlayerSpawnPost(IPlayerSpawnForwardParams @params)
    {
        _appliedTags[@params.Controller.PlayerSlot] = null;
        ApplyClanTag(@params.Controller.PlayerSlot);
        ApplyScore(@params.Controller.PlayerSlot);
    }

    // The scoreboard lists players by score, so points put the best first, and replay bots (the only bots let in)
    // above everyone. Dying changes the score, so it's set again on spawning.
    private void ApplyScore(PlayerSlot slot)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return;
        }

        int score;

        if (controller.IsFakeClient)
        {
            score = int.MaxValue;
        }
        else if (_config.ScoreboardScore)
        {
            score = (int) Math.Min(_points[slot], int.MaxValue);
        }
        else
        {
            return;
        }

        if (controller.Score != score)
        {
            controller.Score = score;
        }
    }

    // A player without a title keeps their own tag, unless they had one of ours.
    private void ApplyClanTag(PlayerSlot slot)
    {
        if (!_config.ScoreboardTags
            || !_bridge.TryGetController(slot, out var controller)
            || controller.IsFakeClient)
        {
            return;
        }

        var tag = TitleOf(slot) is { } title ? RankTitles.Render(_config.ScoreboardFormat, title, _ranks[slot], _total) : null;

        if (tag is null && _appliedTags[slot] is null)
        {
            return;
        }

        tag ??= "";

        if (_appliedTags[slot] != tag || controller.ClanTag != tag)
        {
            controller.SetClanTag(tag);
            _appliedTags[slot] = tag;
        }
    }

    // ------------------------------------------------------------------ chat

    // Rewrites the game's own chat message, so gags, team chat and mutes have already had their say. Colors only render
    // in the message name, not in its parameters, so the whole line goes there.
    private HookReturnValue<NetworkReceiver> OnPostEventPre(IPostEventAbstractHookParams param, HookReturnValue<NetworkReceiver> previous)
    {
        if (param.MsgId != ProtobufNetMessageType.UM_SayText2 || !_config.ChatTags)
        {
            return new (EHookAction.Ignored);
        }

        var data = param.Data;

        if (data.ReadBool("chat") != true
            || data.ReadString("messagename") is not { } token
            || !token.StartsWith("Cstrike_Chat_", StringComparison.Ordinal)
            || data.ReadInt32("entityindex") is not { } index
            || index is < 1 or > 64)
        {
            return new (EHookAction.Ignored);
        }

        var name = data.ReadString("param1");
        var text = data.ReadString("param2");
        var tick = _bridge.GlobalVars.TickCount;

        string line;

        if (_lastChat is { } last && last.Tick == tick && last.Index == index && last.Token == token && last.Name == name && last.Text == text)
        {
            line = last.Line;
        }
        else
        {
            var slot   = (byte) (index - 1);
            var title  = TitleOf(slot);
            var format = title is null ? _config.ChatFormatUntitled : _config.ChatFormat;

            // A leading color code needs something before it to render.
            line      = " " + RankTitles.Render(format, title, _ranks[slot], _total, StripColors(name), StripColors(text), Prefix(token));
            _lastChat = (tick, index, token, name, text, line);
        }

        data.SetString("messagename", line);
        data.SetString("param1", "");
        data.SetString("param2", "");
        data.SetString("param3", "");
        data.SetString("param4", "");

        return new (EHookAction.Ignored);
    }

    // The game's chat tokens: Cstrike_Chat_All, _AllDead, _AllSpec, then team ones like _CT, _T_Dead, _Spec.
    private string Prefix(string token)
    {
        var all  = token.StartsWith("Cstrike_Chat_All", StringComparison.Ordinal);
        var dead = token.Contains("Dead", StringComparison.Ordinal);
        var spec = token.Contains("Spec", StringComparison.Ordinal);

        var prefix = dead ? _config.ChatPrefixDead : spec ? _config.ChatPrefixSpec : "";

        if (!all)
        {
            prefix += _config.ChatPrefixTeam;
        }

        return prefix.Length == 0 ? prefix : RankTitles.Render(prefix, null, 0, 0);
    }

    // Players can't color their name or message.
    private static string StripColors(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = source[i] is >= '\u0001' and <= '\u0010' ? ' ' : source[i];
            }
        });
    }

    // ------------------------------------------------------------------ !ptop

    private ECommandAction OnCommandTop(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out _))
        {
            return ECommandAction.Handled;
        }

        var tr = _localization.For(slot);

        AsyncChatCommand.Run(_bridge, _logger, slot, "GetTopPlayers",
                             () => _request.GetTopPlayers(TopPlayers),
                             (controller, players) =>
                             {
                                 if (players.Count == 0)
                                 {
                                     controller.PrintToChat(tr[ChatTexts.TopPlayersNone]);

                                     return;
                                 }

                                 controller.PrintToChat(tr[ChatTexts.TopPlayersTitle]);

                                 foreach (var p in players)
                                 {
                                     controller.PrintToChat(tr.Format(ChatTexts.TopPlayersRow, p.Rank, Named(p), Points(p.Points)));
                                 }

                                 var rank = _ranks[slot];

                                 controller.PrintToChat(rank > 0
                                                            ? tr.Format(ChatTexts.TopPlayersYou, rank, Math.Max(rank, _total))
                                                            : tr[ChatTexts.TopPlayersUnranked]);
                             });

        return ECommandAction.Handled;
    }

    private string Named(RankedPlayer p)
        => _titles.For(p.Rank, _total) is { } title
            ? ZString.Concat(title.Color, "[", title.Name, "] ", Utils.Highlight(p.Name))
            : Utils.Highlight(p.Name);

    private static string Points(uint points)
        => points.ToString("N0", CultureInfo.InvariantCulture);
}
