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
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Units;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;
using Source2Surf.Timer.Shared.Models.Replay;
using ZstdSharp;

namespace Source2Surf.Timer.Modules;

internal enum WatchResult
{
    Playing,
    Busy,
    NoReplay,
    NoBot,
}

/// <summary>
///     The central replay bot: players pick any run on a leaderboard for it (!replay) and watch it as spectators.
///     Whoever started it controls it, as does anyone the permission provider allows; it plays once, then waits
///     idle on Main and the default style. With spectate_when_idle it waits in spectator, and only joins a team
///     to play.
/// </summary>
internal interface ICentralReplay
{
    /// <summary>
    ///     The central bot, when the replay config has one.
    /// </summary>
    IReplayBotData? CentralBot { get; }

    /// <summary>
    ///     Someone other than <paramref name="slot" /> who started the central bot's replay and is still watching it.
    /// </summary>
    PlayerSlot? BusyFor(PlayerSlot slot);

    /// <summary>
    ///     Whether the player has the central bot's controls (seek, pause, speed, stop) for what's playing: they
    ///     started it, or have <see cref="IPermissionProvider.ReplayControl" />.
    /// </summary>
    bool CanControl(PlayerSlot slot);

    /// <summary>
    ///     Plays a run on the central bot and moves the player to spectate it: a leaderboard row (its
    ///     <paramref name="rank" />), or with rank 0 one of the player's own runs. The server record is already
    ///     loaded; any other run comes from disk, then the replay store. <paramref name="done" /> runs on the game
    ///     thread.
    /// </summary>
    void Watch(PlayerSlot slot, RunRecord record, int rank, Action<WatchResult> done);

    /// <summary>
    ///     Spectates the central bot without taking its controls.
    /// </summary>
    void Spectate(PlayerSlot slot);

    void Seek(PlayerSlot slot, float seconds);

    void TogglePause(PlayerSlot slot);

    void CycleSpeed(PlayerSlot slot);

    /// <summary>
    ///     Back to playing, at the start. The player's own replay stops with them.
    /// </summary>
    void Stop(PlayerSlot slot);
}

internal partial class ReplayPlaybackModule : ICentralReplay
{
    private static readonly float[] CentralSpeeds = [0.5f, 1f, 2f];

    // The team each player left to watch, to go back to.
    private readonly CStrikeTeam[] _returnTeam = new CStrikeTeam[PlayerSlot.MaxPlayerCount];

    private ReplayBotData? Central => _replayBots.Find(static b => b.Type == EReplayBotType.Central);

    public IReplayBotData? CentralBot => Central;

    // The owner keeps the bot while they're out of the game: spectating, or on their way there.
    public PlayerSlot? BusyFor(PlayerSlot slot)
        => Central is { Owner: { } owner } && owner != slot
           && _bridge.EntityManager.FindPlayerControllerBySlot(owner) is { IsValidEntity: true } controller
           && controller.GetPlayerPawn() is not { IsAlive: true }
            ? owner
            : null;

    public void Watch(PlayerSlot slot, RunRecord record, int rank, Action<WatchResult> done)
    {
        if (Central is null)
        {
            done(WatchResult.NoBot);

            return;
        }

        if (BusyFor(slot) is not null)
        {
            done(WatchResult.Busy);

            return;
        }

        if (rank == 1 && _replayCache.TryGetValue((record.Style, record.Track, record.Stage), out var cached))
        {
            StartCentral(slot, cached, record, rank);
            done(WatchResult.Playing);

            return;
        }

        var mapName = _bridge.CurrentMapName;
        var token   = _mapRecordLoadToken.Token;

        Task.Run(async () =>
        {
            try
            {
                var content = LoadRunFromDisk(mapName, record)
                              ?? await LoadRunFromRemote(mapName, record, rank > 0).ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                                                              {
                                                                  if (content is null)
                                                                  {
                                                                      done(WatchResult.NoReplay);
                                                                  }
                                                                  else if (Central is null)
                                                                  {
                                                                      done(WatchResult.NoBot);
                                                                  }
                                                                  else if (BusyFor(slot) is not null)
                                                                  {
                                                                      done(WatchResult.Busy);
                                                                  }
                                                                  else
                                                                  {
                                                                      StartCentral(slot, content, record, rank);
                                                                      done(WatchResult.Playing);
                                                                  }
                                                              },
                                                              token)
                             .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The map changed while it loaded.
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to load the replay of run {RunId}", record.Id);
            }
        }, token);
    }

    public void Spectate(PlayerSlot slot)
    {
        if (Central is not { } bot
            || _bridge.EntityManager.FindPlayerControllerBySlot(slot) is not { IsValidEntity: true } controller)
        {
            return;
        }

        if (controller.Team is CStrikeTeam.TE or CStrikeTeam.CT)
        {
            _returnTeam[slot] = controller.Team;
            controller.ChangeTeam(CStrikeTeam.Spectator);
        }

        // After a team change the observer pawn only exists from the next frame.
        var botSlot = bot.Slot;

        _bridge.ModSharp.InvokeFrameAction(() =>
        {
            if (_bridge.EntityManager.FindPlayerControllerBySlot(slot)?.GetObserverPawn()?.GetObserverService() is not { } observer
                || _bridge.EntityManager.FindPlayerPawnBySlot(botSlot)?.AsPlayerPawn() is not { } botPawn)
            {
                return;
            }

            observer.ObserverMode   = observer.ObserverLastMode = ObserverMode.InEye;
            observer.ObserverTarget = botPawn.Handle;
        });
    }

    public void Seek(PlayerSlot slot, float seconds)
    {
        if (Owned(slot) is not { Frames.Count: > 0 } bot)
        {
            return;
        }

        var frames = (int) MathF.Round(seconds / TimerConstants.TickInterval);
        bot.CurrentFrame = Math.Clamp(bot.CurrentFrame + frames, 0, bot.Frames.Count - 1);
        bot.FrameStep    = 0;

        // Seeking out of the start delay or the pause at the end plays on from there.
        if (bot.Status is EReplayBotStatus.Start or EReplayBotStatus.End)
        {
            StopBotTimer(bot);
            bot.Status = EReplayBotStatus.Running;
        }
    }

    public void TogglePause(PlayerSlot slot)
    {
        if (Owned(slot) is { } bot)
        {
            bot.Paused = !bot.Paused;
        }
    }

    public void CycleSpeed(PlayerSlot slot)
    {
        if (Owned(slot) is { } bot)
        {
            bot.Speed = CentralSpeeds[(Array.IndexOf(CentralSpeeds, bot.Speed) + 1) % CentralSpeeds.Length];
        }
    }

    public bool CanControl(PlayerSlot slot)
        => Owned(slot) is not null;

    public void Stop(PlayerSlot slot)
    {
        if (Owned(slot) is { } bot)
        {
            GoIdle(bot);
        }

        if (_bridge.EntityManager.FindPlayerControllerBySlot(slot) is not { IsValidEntity: true } controller
            || controller.Team != CStrikeTeam.Spectator)
        {
            return;
        }

        // Spawning puts them at the start of their track.
        controller.ChangeTeam(_returnTeam[slot] is CStrikeTeam.TE or CStrikeTeam.CT ? _returnTeam[slot] : CStrikeTeam.CT);
        controller.Respawn();
    }

    private void OnCentralOwnerLeft(PlayerSlot slot)
    {
        // Whoever is watching along can carry on; the bot is free for anyone.
        if (Central is { } bot && bot.Owner == slot)
        {
            bot.Owner = null;
        }

        _returnTeam[slot] = CStrikeTeam.UnAssigned;
    }

    // What's playing, when this player may control it.
    private ReplayBotData? Owned(PlayerSlot slot)
        => Central is { Status: not EReplayBotStatus.Idle } bot
           && (bot.Owner == slot
               || (_bridge.ClientManager.GetGameClient(slot) is { } client
                   && _permissions.HasPermission(client.SteamId, IPermissionProvider.ReplayControl)))
            ? bot
            : null;

    private void StartCentral(PlayerSlot slot, ReplayContent content, RunRecord record, int rank)
    {
        var bot = Central!;

        StopBotTimer(bot);

        bot.Header = content.Header;
        bot.Frames = content.Frames;
        bot.Style  = record.Style;
        bot.Track  = record.Track;
        bot.Stage  = record.Stage;
        bot.Rank   = rank;
        bot.RunId  = record.Id;
        bot.Owner  = slot;
        bot.Paused = false;
        bot.Speed  = 1f;

        JoinGame(bot);
        StartReplay(bot);
        Spectate(slot);
    }

    private void GoIdle(ReplayBotData bot)
    {
        StopBotTimer(bot);

        bot.Owner            = null;
        bot.Status           = EReplayBotStatus.Idle;
        bot.Header           = null;
        bot.Frames           = [];
        bot.CurrentFrame     = 0;
        bot.FrameStep        = 0;
        bot.Paused           = false;
        bot.Speed            = 1f;
        bot.Rank             = 1;
        bot.RunId            = 0;
        bot.Style            = 0;
        bot.Track            = 0;
        bot.Stage            = 0;
        bot.AwaitsNextRecord = false;

        SetupReplayBotName(bot);

        // Whoever was watching it moves on to someone else.
        if (bot.Config.SpectateWhenIdle
            && _bridge.EntityManager.FindPlayerControllerBySlot(bot.Slot) is { IsValidEntity: true } controller
            && controller.Team != CStrikeTeam.Spectator)
        {
            controller.ChangeTeam(CStrikeTeam.Spectator);
        }
    }

    /// <summary>
    ///     A central bot set to wait in spectator, while it has nothing to play. One waiting for the next record stays
    ///     where its watchers are.
    /// </summary>
    private static bool WaitsInSpectator(ReplayBotData bot)
        => bot is { Type: EReplayBotType.Central, Status: EReplayBotStatus.Idle, Config.SpectateWhenIdle: true, AwaitsNextRecord: false };

    // Out of spectator onto CT, with the other replay bots, and spawned, before it plays. The spawn moves it to the start zone;
    // playback places it on the replay's first frame from there.
    private void JoinGame(ReplayBotData bot)
    {
        if (!bot.Config.SpectateWhenIdle
            || _bridge.EntityManager.FindPlayerControllerBySlot(bot.Slot) is not { IsValidEntity: true } controller
            || controller.Team is CStrikeTeam.TE or CStrikeTeam.CT)
        {
            return;
        }

        controller.ChangeTeam(CStrikeTeam.CT);
        controller.Respawn();
    }

    private void StopBotTimer(ReplayBotData bot)
    {
        if (bot.Timer is { } timer && _bridge.ModSharp.IsValidTimer(timer))
        {
            _bridge.ModSharp.StopTimer(timer);
        }

        bot.Timer = null;
    }

    // A best run (PB or WR when it was set) keeps its file; a slower one is among its player's recent runs.
    private ReplayContent? LoadRunFromDisk(string mapName, RunRecord record)
    {
        string[] paths =
        [
            ReplayShared.BuildReplayPath(_replayDirectory, mapName, record.Style, record.Track, record.Stage, record.Id),
            ReplayShared.BuildRecentRunPath(_replayDirectory, mapName, record.Style, record.Track, record.Stage, record.SteamId, record.Id),
        ];

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            using var decompressor = new Decompressor();

            if (ReplayShared.LoadReplayFromPath(path, record.Style, record.Track, record.Stage, decompressor, _logger) is { } result)
            {
                return result.Content;
            }
        }

        return null;
    }

    // That run by its id; for a leaderboard row, whose run is its player's best, also their best by SteamID, which
    // a store without run lookups still has.
    private async Task<ReplayContent?> LoadRunFromRemote(string mapName, RunRecord record, bool best)
    {
        if (!_replayProviderProxy.IsAvailable)
        {
            return null;
        }

        var bytes = await _replayProviderProxy.GetRunReplayAsync((ulong) record.Id).ConfigureAwait(false);

        if (bytes is not null)
        {
            return DeserializeAndCache(bytes, mapName, record.Style, record.Track, record.Stage, record.Id);
        }

        if (best)
        {
            bytes = record.Stage == 0
                ? await _replayProviderProxy.GetReplayAsync(mapName, record.Style, record.Track, record.SteamId).ConfigureAwait(false)
                : await _replayProviderProxy.GetStageReplayAsync(mapName, record.Style, record.Track, record.Stage, record.SteamId)
                                            .ConfigureAwait(false);
        }

        return bytes is null ? null : ReplayShared.DeserializeReplay(bytes, record.Style, record.Track, record.Stage, _logger)?.Content;
    }

    // Only a replay fetched by its run id is surely that run's, so only those are saved to disk.
    private ReplayContent? DeserializeAndCache(byte[] bytes, string mapName, int style, int track, int stage, long runId)
    {
        if (ReplayShared.DeserializeReplay(bytes, style, track, stage, _logger) is not { } loaded)
        {
            return null;
        }

        ReplayShared.CacheDownloadedReplay(ReplayShared.BuildReplayPath(_replayDirectory, mapName, style, track, stage, runId),
                                           bytes,
                                           _logger);

        return loaded.Content;
    }
}
