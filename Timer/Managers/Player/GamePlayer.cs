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
using System.Runtime.CompilerServices;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;
using Sharp.Shared.Units;

namespace SurfTimer.Managers.Player;

internal interface IGamePlayer : IEquatable<IGamePlayer>
{
    IGameClient Client { get; }

    PlayerSlot  Slot            { get; }
    EntityIndex ControllerIndex { get; }
    SteamID     SteamId         { get; }
    bool        IsFakeClient    { get; }
    string      Name            { get; }

    IPlayerController? Controller { get; }

    bool IsAdmin { get; }

    Guid DatabaseId { get; }
}

internal class GamePlayer(IGameClient client) : IGamePlayer
{
    private bool _valid;

    public bool        Authenticated { get; private set; }
    public IGameClient Client        { get; private set; } = client;
    public PlayerSlot  Slot          { get; }              = client.Slot;

    public EntityIndex        ControllerIndex { get; }              = client.ControllerIndex;
    public SteamID            SteamId         { get; private set; } = client.SteamId;
    public bool               IsFakeClient    { get; }              = client.IsFakeClient;
    public string             Name            { get; private set; } = client.Name;
    public IPlayerController? Controller      { get; private set; }
    public bool               IsAdmin         { get; } = true; /*TODO: Change this*/
    public Guid               DatabaseId      { get; private set; }

    public bool Equals(IGamePlayer? other)
        => other is not null && Client.Equals(other.Client) && SteamId.Equals(other.SteamId) && Slot.Equals(other.Slot);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Invalidate()
    {
        _valid     = false;
        Controller = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsValid()
        => _valid && Controller is not null && Controller.IsValid() && Client.IsValid;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetAuthenticated()
        => Authenticated = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetController(IPlayerController? controller)
    {
        _valid     = true;
        Controller = controller;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateClient(IGameClient client)
    {
        Client  = client;
        SteamId = client.SteamId;
        Name    = client.Name;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetDatabaseId(Guid id)
        => DatabaseId = id;
}
