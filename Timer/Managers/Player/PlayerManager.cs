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
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Configuration;
using Source2Surf.Timer.Managers.Submission;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Interfaces.Listeners;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Managers.Player;

internal interface IPlayerManager
{
    void RegisterListener(IPlayerManagerListener listener);

    void UnregisterListener(IPlayerManagerListener listener);

    PlayerProfile? GetPlayerProfile(PlayerSlot slot);

    PlayerProfile? GetPlayerProfile(SteamID steamId);
}

internal class PlayerManager : IManager, IPlayerManager, IClientListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 1;

    private readonly InterfaceBridge        _bridge;
    private readonly IRequestManager        _requestManager;
    private readonly ScoreWriteModeOptions  _scoreWriteMode;
    private readonly RunSubmissionSender    _runSubmissionSender;
    private readonly ILogger<PlayerManager> _logger;

    // Core storage: PlayerProfile indexed by PlayerSlot
    private readonly PlayerProfile?[] _profiles;

    // Reverse index: SteamID -> PlayerSlot for O(1) lookup
    private readonly Dictionary<SteamID, PlayerSlot> _steamIdToSlot = new();

    // Temporary storage: pending SteamID between OnClientConnected and OnClientPostAdminCheck
    private readonly SteamID?[] _pendingSteamIds;

    // Temporary storage: whether a slot has completed authentication
    private readonly bool[] _authenticated;
    private readonly CancellationTokenSource?[] _profileLoadCancellations;
    private readonly long[] _profileSessionEpoch;
    private readonly object _profileLoadGate = new ();

    // Listener hub for notifying consumers
    private readonly ListenerHub<IPlayerManagerListener> _listenerHub;

    public PlayerManager(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ScoreWriteModeOptions  scoreWriteMode,
                         RunSubmissionSender    runSubmissionSender,
                         ILogger<PlayerManager> logger)
    {
        _bridge         = bridge;
        _requestManager = requestManager;
        _scoreWriteMode = scoreWriteMode;
        _runSubmissionSender = runSubmissionSender;
        _logger         = logger;

        _profiles        = new PlayerProfile?[PlayerSlot.MaxPlayerCount];
        _pendingSteamIds = new SteamID?[PlayerSlot.MaxPlayerCount];
        _authenticated   = new bool[PlayerSlot.MaxPlayerCount];
        _profileLoadCancellations = new CancellationTokenSource?[PlayerSlot.MaxPlayerCount];
        _profileSessionEpoch = new long[PlayerSlot.MaxPlayerCount];
        _listenerHub     = new ListenerHub<IPlayerManagerListener>(logger);
    }

    public void OnClientConnected(IGameClient client)
    {
        var slot = (int) client.Slot;

        CancelProfileLoad(slot);
        _profileSessionEpoch[slot]++;

        // Clean up old data if slot is occupied
        if (_profiles[slot] is { } oldProfile)
        {
            RemoveSteamIdMappingIfSlotMatches(oldProfile.SteamId, client.Slot);
            _profiles[slot] = null;
        }

        if (_pendingSteamIds[slot] is { } oldPending)
        {
            RemoveSteamIdMappingIfSlotMatches(oldPending, client.Slot);
        }

        _pendingSteamIds[slot] = client.SteamId;
        _authenticated[slot]   = false;
    }

    public void OnClientPutInServer(IGameClient client)
        => _listenerHub.NotifyAll("OnClientPutInServer",
                                  static (l, s) => l.OnClientPutInServer(s),
                                  client.Slot);

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (client.IsFakeClient)
        {
            return;
        }

        var slot = client.Slot;

        // Verify SteamID consistency
        if (_pendingSteamIds[slot] is not { } pendingSteamId || pendingSteamId != client.SteamId)
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");

            _logger.LogError("Player {@client} SteamID mismatch: pending={pendingSteamId}, actual={actualSteamId} at slot<{slot}>",
                             client,
                             _pendingSteamIds[slot],
                             client.SteamId,
                             client.Slot);

            _bridge.ClientManager.KickClient(client, "Invalid SteamId", NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        // Check for duplicate authentication
        if (_authenticated[slot])
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");
            _logger.LogError("Player {@client} was already fully Authenticated!", client);

            _bridge.ClientManager.KickClient(client,
                                             "Invalid Steam Authenticated",
                                             NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        _authenticated[slot] = true;

        var steamId = client.SteamId;
        var name    = client.Name;
        var profileSessionEpoch = _profileSessionEpoch[slot];
        var profileLoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_bridge.CancellationToken);
        lock (_profileLoadGate)
        {
            _profileLoadCancellations[slot] = profileLoadCancellation;
        }

        Task.Run(async () =>
        {
            try
            {
                var profile = await LoadPlayerProfileAsync(steamId, name, profileLoadCancellation.Token)
                                    .ConfigureAwait(false);

                await _bridge.ModSharp.InvokeFrameActionAsync(() =>
                {
                    if (_profileSessionEpoch[slot] != profileSessionEpoch)
                    {
                        return;
                    }

                    if (_pendingSteamIds[slot] is not { } pending)
                    {
                        return;
                    }

                    if (pending != steamId)
                    {
                        return;
                    }

                    if (_bridge.ClientManager.GetGameClient(steamId) is not { } c)
                    {
                        return;
                    }

                    if (c.Slot != slot)
                    {
                        return;
                    }

                    _profiles[slot]         = profile;
                    _steamIdToSlot[steamId] = slot;
                    _pendingSteamIds[slot]  = null;

                    _listenerHub.NotifyAll("OnClientInfoLoaded",
                                           static (l, s) => l.OnClientInfoLoaded(s),
                                           steamId);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (profileLoadCancellation.IsCancellationRequested)
            {
                // Disconnect and shutdown intentionally abandon this one player session.
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error when loading player profile for {steamId}", steamId);
                try
                {
                    await HandlePermanentProfileLoadFailureAsync((int)slot, steamId, profileSessionEpoch)
                        .ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    _logger.LogError(failure,
                        "Could not close failed backend player-profile session for {steamId} at slot {slot}",
                        steamId, slot);
                }
            }
            finally
            {
                lock (_profileLoadGate)
                {
                    if (ReferenceEquals(_profileLoadCancellations[slot], profileLoadCancellation))
                    {
                        _profileLoadCancellations[slot] = null;
                    }
                }

                profileLoadCancellation.Dispose();
            }
        });
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        var slot = (int) client.Slot;

        CancelProfileLoad(slot);
        _profileSessionEpoch[slot]++;

        _listenerHub.NotifyAll("OnClientDisconnected",
                               static (l, s) => l.OnClientDisconnected(s),
                               client.Slot);

        if (_profiles[slot] is { } profile)
        {
            RemoveSteamIdMappingIfSlotMatches(profile.SteamId, client.Slot);
            _profiles[slot] = null;
        }

        if (_pendingSteamIds[slot] is { } pending)
        {
            RemoveSteamIdMappingIfSlotMatches(pending, client.Slot);
            _pendingSteamIds[slot] = null;
        }

        _authenticated[slot] = false;
    }

    public PlayerProfile? GetPlayerProfile(PlayerSlot slot)
    {
        var index = (int) slot;

        if (index < 0 || index >= _profiles.Length)
        {
            return null;
        }

        return _profiles[index];
    }

    public PlayerProfile? GetPlayerProfile(SteamID steamId)
    {
        if (_steamIdToSlot.TryGetValue(steamId, out var slot))
        {
            var profile = _profiles[(int) slot];

            if (profile is not null && profile.SteamId == steamId)
            {
                return profile;
            }
        }

        return null;
    }

    public void RegisterListener(IPlayerManagerListener listener)
        => _listenerHub.Register(listener);

    public void UnregisterListener(IPlayerManagerListener listener)
        => _listenerHub.Unregister(listener);

    public bool Init()
    {
        _bridge.ClientManager.InstallClientListener(this);

        return true;
    }

    public void OnPostInit()
    {
    }

    public void Shutdown()
    {
        for (var slot = 0; slot < _profileLoadCancellations.Length; slot++)
        {
            CancelProfileLoad(slot);
            _profileSessionEpoch[slot]++;
        }

        _bridge.ClientManager.RemoveClientListener(this);
        _listenerHub.Clear();
        _steamIdToSlot.Clear();
    }

    private async Task<PlayerProfile> LoadPlayerProfileAsync(SteamID steamId,
                                                              string name,
                                                              CancellationToken cancellationToken)
    {
        if (_scoreWriteMode.Mode != ScoreWriteMode.RemoteWrite)
        {
            return await RetryHelper.RetryAsync(() => _requestManager.GetPlayerProfile(steamId, name),
                                                RetryHelper.IsTransient,
                                                _logger,
                                                "GetPlayerProfile",
                                                cancellationToken: cancellationToken)
                                    .ConfigureAwait(false);
        }

        var steamIdValue = checked((long)steamId.AsPrimitive());
        var safeName = NormalizeRemotePlayerName(name, steamIdValue);
        var retryDelay = TimeSpan.FromMilliseconds(500);

        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await _runSubmissionSender.EnsurePlayerProfileAsync(
                    steamIdValue, safeName, cancellationToken).ConfigureAwait(false);
                return BackendPlayerProfileMapper.ToProfile(response, steamId);
            }
            catch (RpcException exception) when (IsRetryableProfileRpcFailure(exception))
            {
                _logger.LogWarning(exception,
                    "Backend player profile unavailable for {SteamId}; retrying in {Delay}.",
                    steamId, retryDelay);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 30_000));
            }
        }
    }

    private static bool IsRetryableProfileRpcFailure(RpcException exception)
        => exception.StatusCode is StatusCode.Unavailable
            or StatusCode.DeadlineExceeded
            or StatusCode.Cancelled
            or StatusCode.Internal
            or StatusCode.Unknown
            or StatusCode.ResourceExhausted
            // A proxy's plain 404 during a backend redeploy is not a backend verdict.
            || RunSubmissionSender.IsHttpIntermediaryNotFound(exception);

    private static string NormalizeRemotePlayerName(string? name, long steamId)
    {
        // Strip control characters before trimming: removing one can expose surrounding
        // whitespace, and the backend rejects names with leading/trailing whitespace.
        var normalized = string.Concat((name ?? string.Empty).Where(character => !char.IsControl(character))).Trim();
        if (normalized.Length > 192)
        {
            normalized = normalized[..192].TrimEnd();
        }

        return normalized.Length == 0 ? $"Player {steamId}" : normalized;
    }

    private void CancelProfileLoad(int slot)
    {
        CancellationTokenSource? cancellation;
        lock (_profileLoadGate)
        {
            cancellation = _profileLoadCancellations[slot];
            _profileLoadCancellations[slot] = null;
        }

        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completed profile task owns disposal. It cannot still apply a result.
        }
    }

    private void RemoveSteamIdMappingIfSlotMatches(SteamID steamId, PlayerSlot slot)
    {
        if (_steamIdToSlot.TryGetValue(steamId, out var mappedSlot) && mappedSlot == slot)
        {
            _steamIdToSlot.Remove(steamId);
        }
    }

    private async Task HandlePermanentProfileLoadFailureAsync(int slot,
                                                               SteamID steamId,
                                                               long sessionEpoch)
    {
        try
        {
            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (_profileSessionEpoch[slot] != sessionEpoch
                    || _pendingSteamIds[slot] != steamId)
                {
                    return;
                }

                _pendingSteamIds[slot] = null;
                _authenticated[slot] = false;
                if (_bridge.ClientManager.GetGameClient(steamId) is { } client
                    && (int)client.Slot == slot)
                {
                    _bridge.ClientManager.KickClient(client,
                        "Backend player profile unavailable",
                        NetworkDisconnectionReason.Kicked);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_bridge.CancellationToken.IsCancellationRequested)
        {
            // Shutdown is already disconnecting clients and abandoning pending profiles.
        }
    }
}
