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

using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Listeners;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;
using SurfTimer.Managers.Player;
using ZLinq;

namespace SurfTimer.Managers;

internal interface IPlayerManager
{
    delegate void GameClientDelegate(IGamePlayer client);

    public event GameClientDelegate ClientPutInServer;
    public event GameClientDelegate ClientDisconnected;
    public event GameClientDelegate ClientInfoLoaded;

    IGamePlayer? GetPlayer(PlayerSlot slot);

    IGamePlayer? GetPlayer(SteamID steamId);

    IGamePlayer? GetPlayer(IGameClient client);

    IGamePlayer[] GetPlayers(bool ignoreFakeClient = true);
}

internal class PlayerManager : IManager, IPlayerManager, IClientListener
{
    public int ListenerVersion  => IGameListener.ApiVersion;
    public int ListenerPriority => 1;

    private readonly InterfaceBridge _bridge;
    private readonly IRequestManager _requestManager;

    private readonly ILogger<PlayerManager> _logger;
    private readonly GamePlayer?[]          _players;

    public PlayerManager(InterfaceBridge        bridge,
                         IRequestManager        requestManager,
                         ILogger<PlayerManager> logger)

    {
        _bridge         = bridge;
        _requestManager = requestManager;

        _logger    = logger;

        _players = Enumerable.Repeat<GamePlayer?>(null, PlayerSlot.MaxPlayerSlot).ToArray();
    }

    public void OnClientConnected(IGameClient client)
    {
        if (_players[client.Slot] is { } old)
        {
            if (old.Client.Equals(client) && old.SteamId == client.SteamId)
            {
                _logger.LogWarning("Double connection with same slot. old: {old}, new: {new}", old.Client, client);

                return;
            }
        }

        _players[client.Slot] = new (client);
    }

    public void OnClientPutInServer(IGameClient client)
    {
        if (_players[client.Slot] is not { } player || player.SteamId != client.SteamId)
        {
            return;
        }

        player.UpdateClient(client);
        player.SetController(_bridge.EntityManager.FindEntityByIndex<IPlayerController>(client.ControllerIndex));

        ClientPutInServer?.Invoke(player);
    }

    public void OnClientPostAdminCheck(IGameClient client)
    {
        if (_players[client.Slot] is not { } player || !player.Client.Equals(client))
        {
            return;
        }

        if (!player.IsValid() || player.Controller is null)
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");
            _logger.LogError("Player {@client} does not exists in pool or controller is null", client);

            _bridge.ClientManager.KickClient(client,
                                             "Invalid Player or Controller",
                                             NetworkDisconnectionReason.BadDeltaTick);

            return;
        }

        if (client.SteamId != player.SteamId)
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");

            _logger.LogError("Player {@client} with same pointer mismatch steamId {steamId} at slot<{slot}>",
                             client,
                             player.SteamId,
                             player.Slot);

            _bridge.ClientManager.KickClient(client, "Invalid SteamId", NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        if (player.Authenticated)
        {
            using var scope = _logger.BeginScope("OnClientPostAdminCheck");
            _logger.LogError("Player {@client} was already fully Authenticated!", player);

            _bridge.ClientManager.KickClient(client,
                                             "Invalid Steam Authenticated",
                                             NetworkDisconnectionReason.SteamAuthInvalid);

            return;
        }

        player.SetAuthenticated();
        player.UpdateClient(client);

        var steamId = player.SteamId;
        var name    = player.Name;

        Task.Run(async () =>
        {
            var userinfo = await _requestManager.GetPlayerProfile(steamId, name).ConfigureAwait(false);

            await _bridge.ModSharp.InvokeFrameActionAsync(() =>
            {
                if (GetPlayer(steamId) is { } target)
                {
                    _players[target.Slot]?.SetDatabaseId(userinfo.Id);
                    ClientInfoLoaded?.Invoke(target);
                }
            }).ConfigureAwait(false);
        });
    }

    public void OnClientDisconnected(IGameClient client, NetworkDisconnectionReason reason)
    {
        if (_players[client.Slot] is not { } player || !player.Client.Equals(client))
        {
            return;
        }

        ClientDisconnected?.Invoke(player);

        player.Invalidate();
        _players[client.Slot] = null;
    }

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
        _bridge.ClientManager.RemoveClientListener(this);

        ClientPutInServer  = null;
        ClientDisconnected = null;
        ClientInfoLoaded   = null;
    }

    public event IPlayerManager.GameClientDelegate? ClientPutInServer;
    public event IPlayerManager.GameClientDelegate? ClientDisconnected;
    public event IPlayerManager.GameClientDelegate? ClientInfoLoaded;

    public IGamePlayer? GetPlayer(PlayerSlot slot)
        => _players[slot];

    public IGamePlayer? GetPlayer(SteamID steamId)
    {
        return _players.AsValueEnumerable().FirstOrDefault(x => x is not null && x.IsValid() && x.SteamId == steamId);
    }

    public IGamePlayer? GetPlayer(IGameClient client)
    {
        return _players.AsValueEnumerable().FirstOrDefault(x => x is not null && x.IsValid() && x.Client.Equals(client));
    }

    public IGamePlayer[] GetPlayers(bool ignoreFakeClient = true)
    {
        return _players.Where(Filter)!
                       .ToArray<IGamePlayer>();

        bool Filter(GamePlayer? player)
        {
            if (player is null || !player.IsValid())
            {
                return false;
            }

            if (ignoreFakeClient && player.IsFakeClient)
            {
                return false;
            }

            return player.Client.SignOnState >= SignOnState.Connected;
        }
    }
}
