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
using Microsoft.Extensions.Logging;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Models;
using Timer.MapChooser.Logic;

namespace Timer.MapChooser;

public sealed partial class MapChooserModule
{
    private const string AdminPermission = "mapchooser:admin";
    private const string MapPermission   = "admin:map"; // the permission of ModSharp's own !map

    private void RegisterCommands()
    {
        if (!_commandsRegistered && _commandCenter?.Instance is { } center)
        {
            var registry = center.GetRegistry(ModuleIdentity);

            registry.RegisterClientCommand("rtv", (client, _) => OnRockTheVote(client));
            registry.RegisterClientCommand("rockthevote", (client, _) => OnRockTheVote(client));
            registry.RegisterClientCommand("unrtv", (client, _) => OnUnrockTheVote(client));
            registry.RegisterClientCommand("nominate", OnNominate);
            registry.RegisterClientCommand("nom", OnNominate);
            registry.RegisterClientCommand("unnominate", (client, _) => OnUnnominate(client));
            registry.RegisterClientCommand("unnom", (client, _) => OnUnnominate(client));
            registry.RegisterClientCommand("nominations", (client, _) => OnNominations(client));
            registry.RegisterClientCommand("noms", (client, _) => OnNominations(client));
            registry.RegisterClientCommand("nextmap", (client, _) => OnNextMap(client));
            registry.RegisterClientCommand("timeleft", (client, _) => OnTimeLeft(client));

            _commandsRegistered = true;
        }

        if (!_adminCommandsRegistered && _commandsRegistered && _adminManager?.Instance is { } admins)
        {
            try
            {
                var registry = admins.GetCommandRegistry(ModuleIdentity);
                registry.RegisterPermissions([AdminPermission]);

                registry.RegisterAdminCommand("forcevote", OnForceVote, [AdminPermission, MapPermission]);
                registry.RegisterAdminCommand("cancelvote", OnCancelVote, [AdminPermission, MapPermission]);
                registry.RegisterAdminCommand("setnextmap", OnSetNextMap, [AdminPermission, MapPermission]);
                registry.RegisterAdminCommand("extend", OnExtend, [AdminPermission, MapPermission]);

                _adminCommandsRegistered = true;
            }
            catch (InvalidOperationException)
            {
                // CommandCenter isn't up yet; retried when it connects.
            }
        }
    }

    // ------------------------------------------------------------------ players

    private void OnRockTheVote(IGameClient client)
    {
        if (!_mapRunning)
        {
            return;
        }

        var now    = Now;
        var needed = RockTheVote.Needed(Players(), _config.RtvRatio);

        if (!float.IsNaN(_changeAt))
        {
            Reply(client, ChooserTexts.RtvChanging);
        }
        else if (_vote is not null)
        {
            Reply(client, ChooserTexts.VoteRunning);
        }
        else if (_clock.Elapsed(now) < _config.RtvDelay)
        {
            Reply(client, ChooserTexts.RtvTooSoon, Duration(_config.RtvDelay - _clock.Elapsed(now)));
        }
        else if (!_rtv.Add(client.Slot))
        {
            Reply(client, ChooserTexts.RtvAlready, _rtv.Count, needed);
        }
        else
        {
            Changed();
            Announce(ChooserTexts.RtvWants, client.Name, _rtv.Count, needed);
            CheckRockTheVote();
        }
    }

    private void OnUnrockTheVote(IGameClient client)
    {
        if (!_rtv.Remove(client.Slot))
        {
            return;
        }

        Changed();
        Reply(client, ChooserTexts.RtvRemoved,
              _rtv.Count, RockTheVote.Needed(Players(), _config.RtvRatio));
    }

    private void OnNominate(IGameClient client, StringCommand command)
    {
        var arg = command.ArgString.Trim();

        if (arg.Length == 0)
        {
            OpenNominateFromChat(client);

            return;
        }

        if (int.TryParse(arg, out var tier) && tier is >= 1 and <= 8)
        {
            OpenNominateMenu(client, null, tier);

            return;
        }

        if (_poolByName.TryGetValue(arg, out var exact))
        {
            NominateFromChat(client, exact.Name);

            return;
        }

        var matches = _pool.Where(x => x.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).ToList();

        switch (matches.Count)
        {
            case 0:
                Reply(client, ChooserTexts.NominateNoMatch, arg);

                break;
            case 1:
                NominateFromChat(client, matches[0]);

                break;
            default:
                OpenNominateMenu(client, arg, 0);
                Reply(client, ChooserTexts.NominateMatches, matches.Count,
                      string.Join(", ", matches.Take(6)) + (matches.Count > 6 ? ", ..." : string.Empty));

                break;
        }
    }

    private void OpenNominateFromChat(IGameClient client)
    {
        OpenNominateMenu(client, null, 0);
        Reply(client, ChooserTexts.NominateUsage);
    }

    private void NominateFromChat(IGameClient client, string map)
    {
        switch (NominateCore(client.Slot, map))
        {
            case NominateResult.AlreadyNominated:
                Reply(client, ChooserTexts.NominateTaken, Map(map));

                break;
            case NominateResult.Full:
                Reply(client, ChooserTexts.NominateFull);

                break;
            case NominateResult.CurrentMap:
                Reply(client, ChooserTexts.NominateCurrent);

                break;
            case NominateResult.Recent:
                Reply(client, ChooserTexts.NominateRecent, Map(map));

                break;
            case NominateResult.NotFound:
                Reply(client, ChooserTexts.NominateNotFound, map);

                break;
            case NominateResult.Closed:
                Reply(client, ChooserTexts.NominateClosed);

                break;
        }
    }

    private void OnUnnominate(IGameClient client)
    {
        if (_nominations.Remove(client.Slot) is not { } map)
        {
            Reply(client, ChooserTexts.NominateNone);

            return;
        }

        Changed();
        Reply(client, ChooserTexts.NominateRemoved, Map(map));
    }

    private void OnNominations(IGameClient client)
    {
        if (_nominations.Count == 0)
        {
            Reply(client, ChooserTexts.NominationsNone);

            return;
        }

        Reply(client, ChooserTexts.Nominations, string.Join(", ", _nominations.Maps.Select(Map)));
    }

    private void OnNextMap(IGameClient client)
    {
        if (_nextMap is { } map)
        {
            Reply(client, ChooserTexts.Nextmap, Map(map));
        }
        else
        {
            Reply(client, ChooserTexts.NextmapPending);
        }
    }

    private void OnTimeLeft(IGameClient client)
    {
        if (_mapRunning)
        {
            Reply(client, ChooserTexts.Timeleft, Duration(_clock.TimeLeft(Now)));
        }
    }

    // ------------------------------------------------------------------ admins

    private void OnForceVote(IGameClient? issuer, StringCommand command)
    {
        if (!_mapRunning)
        {
            return;
        }

        if (_vote is not null || !float.IsNaN(_changeAt))
        {
            AdminReply(issuer, ChooserTexts.AdminBusy);

            return;
        }

        _nextMap = null;
        StartVote(MapVoteKind.EndOfMap, Now);
    }

    private void OnCancelVote(IGameClient? issuer, StringCommand command)
    {
        if (_vote is null)
        {
            AdminReply(issuer, ChooserTexts.AdminNoVote);

            return;
        }

        _vote = null;
        Changed();
        Announce(ChooserTexts.VoteCancelled);
    }

    private void OnSetNextMap(IGameClient? issuer, StringCommand command)
    {
        var arg = command.ArgString.Trim();

        var map = _poolByName.GetValueOrDefault(arg)?.Name
                  ?? _modSharp.ListWorkshopMaps().Select(x => x.Name).FirstOrDefault(x => x.Equals(arg, StringComparison.OrdinalIgnoreCase))
                  ?? (arg.Length > 0 && _modSharp.IsMapValid(arg) ? arg : null);

        if (map is null)
        {
            AdminReply(issuer, ChooserTexts.NominateNotFound, arg);

            return;
        }

        _nextMap = map;
        Changed();
        Announce(ChooserTexts.NextmapSet, Map(map));
    }

    private void OnExtend(IGameClient? issuer, StringCommand command)
    {
        if (!_mapRunning)
        {
            return;
        }

        var minutes = int.TryParse(command.ArgString.Trim(), out var value) && value > 0 ? value : _config.ExtendMinutes;

        _clock.Extend(minutes, false);
        _changeAt = float.NaN;
        Changed();
        Announce(ChooserTexts.VoteExtended, minutes);
    }

    private void AdminReply(IGameClient? issuer, ChooserText text, params object?[] args)
    {
        if (issuer is not null)
        {
            Reply(issuer, text, args);
        }
        else
        {
            _logger.LogInformation("{message}", string.Format(text.English, args));
        }
    }
}
