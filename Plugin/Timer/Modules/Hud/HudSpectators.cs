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
using Cysharp.Text;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Hud;

namespace Source2Surf.Timer.Modules;

// The spectator list (like bhoptimer's): who watches the HUD's source, one name a line, seven at most, right centre.
internal partial class HudModule
{
    internal const int SpecRows = 7;

    private const float SpecRebuildInterval = 0.25f;
    private const int   SpecNameMax         = 20;

    internal static readonly string[] SpecNameIds = Enumerable.Range(0, SpecRows).Select(i => ZString.Concat("SpecName", i)).ToArray();

    private readonly List<string>[] _spectators = Enumerable.Range(0, PlayerSlot.MaxPlayerCount).Select(_ => new List<string>()).ToArray();
    private          float          _spectatorsAt = float.NegativeInfinity;

    // Who spectates whom, for every HUD at once, a few times a second: the same rule as !specs.
    private void RebuildSpectators(float now)
    {
        if (now >= _spectatorsAt && now - _spectatorsAt < SpecRebuildInterval)
        {
            return;
        }

        _spectatorsAt = now;

        foreach (var list in _spectators)
        {
            list.Clear();
        }

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (!client.IsFakeClient
                && !client.IsHltv
                && client.GetPlayerController() is { } spectator
                && _bridge.GetObservedSlot(spectator) is { } target
                && target != client.Slot)
            {
                _spectators[target].Add(client.Name);
            }
        }
    }

    private void UpdateSpectators(HudWriter w, HudPlayer p, List<string> names)
    {
        // The map vote's panel takes the same spot while it runs, unless this one was dragged elsewhere.
        w.Class("SpecPanel", "voting", _mapChooser.Vote is not null && p.Positions[(int) HudTarget.Spec] is null);

        // Hidden with nobody watching, except in the menu, where it can be dragged.
        var shown = names.Count > 0 || p.MenuOpen;
        w.Class("SpecList", "Hidden", !shown);

        if (!shown)
        {
            return;
        }

        w.Text("SpecHead", "text", p.Tr.Format(HudTexts.SpecHead, names.Count));

        for (var i = 0; i < SpecRows; i++)
        {
            var on = i < names.Count;
            w.Class(SpecNameIds[i], "Hidden", !on);

            if (on)
            {
                w.Text(SpecNameIds[i], "text", Shorten(names[i]));
            }
        }

        w.Class("SpecMore", "Hidden", names.Count <= SpecRows);
    }

    private static string Shorten(string name)
    {
        if (name.Length <= SpecNameMax)
        {
            return name;
        }

        // Not through the middle of a surrogate pair.
        var cut = char.IsHighSurrogate(name[SpecNameMax - 2]) ? SpecNameMax - 2 : SpecNameMax - 1;

        return string.Concat(name.AsSpan(0, cut), "…");
    }
}
