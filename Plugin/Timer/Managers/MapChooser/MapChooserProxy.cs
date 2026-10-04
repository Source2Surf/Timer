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

using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;
using Source2Surf.Timer.Shared.Models;

namespace Source2Surf.Timer.Managers.MapChooser;

/// <summary>
///     The Timer.MapChooser module when it's loaded, else a chooser with nothing to show.
/// </summary>
internal sealed class MapChooserProxy : ExternalModuleProxy<IMapChooser>, IMapChooser
{
    public MapChooserProxy(ISharedSystem shared, ILogger<MapChooserProxy> logger)
        : base(shared, new NoMapChooser(), logger)
    {
    }

    protected override string Identity     => IMapChooser.Identity;
    protected override string ContractName => "IMapChooser";

    protected override bool InitFallback()
        => true;

    protected override void ShutdownFallback()
    {
    }

    public int         Version   => Current.Version;
    public IMapVote?   Vote      => Current.Vote;
    public MapVoteKeys VoteKeys  => Current.VoteKeys;
    public string?     NextMap   => Current.NextMap;
    public float       TimeLeft  => Current.TimeLeft;

    public int GetVoteChoice(PlayerSlot slot)
        => Current.GetVoteChoice(slot);

    public int GetVoteCursor(PlayerSlot slot)
        => Current.GetVoteCursor(slot);

    public void CastVote(PlayerSlot slot, int option)
        => Current.CastVote(slot, option);

    public NominateMenu? GetNominateMenu(PlayerSlot slot)
        => Current.GetNominateMenu(slot);

    public void SetNominateFilter(PlayerSlot slot, int tier, bool unfinishedOnly)
        => Current.SetNominateFilter(slot, tier, unfinishedOnly);

    public void CloseNominateMenu(PlayerSlot slot)
        => Current.CloseNominateMenu(slot);

    public NominateResult Nominate(PlayerSlot slot, string map)
        => Current.Nominate(slot, map);

    private sealed class NoMapChooser : IMapChooser
    {
        public int         Version  => 0;
        public IMapVote?   Vote     => null;
        public MapVoteKeys VoteKeys => new ("autobuy", "rebuy", "lookatweapon");
        public string?     NextMap  => null;
        public float       TimeLeft => 0;

        public int GetVoteChoice(PlayerSlot slot)
            => -1;

        public int GetVoteCursor(PlayerSlot slot)
            => 0;

        public void CastVote(PlayerSlot slot, int option)
        {
        }

        public NominateMenu? GetNominateMenu(PlayerSlot slot)
            => null;

        public void SetNominateFilter(PlayerSlot slot, int tier, bool unfinishedOnly)
        {
        }

        public void CloseNominateMenu(PlayerSlot slot)
        {
        }

        public NominateResult Nominate(PlayerSlot slot, string map)
            => NominateResult.Closed;
    }
}
