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
using System.Runtime.InteropServices;
using System.Linq;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models;

namespace Timer.MapChooser;

public sealed partial class MapChooserModule
{

    /// <summary>
    ///     The text in the player's language, from the timer's locale, or else in English.
    /// </summary>
    private string Text(IGameClient client, ChooserText text)
        => ChatColorTags.Apply(_localization?.Instance?.GetText(client.Slot, text.Key) ?? text.English);

    /// <summary>
    ///     A message to one player, in their language when the timer's locale has the key.
    /// </summary>
    private void Reply(IGameClient client, ChooserText text, params object?[] args)
        => client.Print(HudPrintChannel.Chat, _prefix + Format(Text(client, text), text, args));

    private static string Format(string template, ChooserText text, object?[] args)
    {
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return string.Format(text.English, args);
        }
    }

    private IEnumerable<IGameClient> Humans()
        => _clients.GetGameClients(true).Where(x => !x.IsFakeClient && !x.IsHltv);

    // To every player: made and sent once per language.
    private void Announce(ChooserText text, params object?[] args)
    {
        var byText = new Dictionary<string, ulong>();

        foreach (var client in Humans())
        {
            ref var players = ref CollectionsMarshal.GetValueRefOrAddDefault(byText, Text(client, text), out _);
            players |= 1UL << client.Slot;
        }

        foreach (var (template, players) in byText)
        {
            _modSharp.PrintChannelFilter(HudPrintChannel.Chat, _prefix + Format(template, text, args), new RecipientFilter(players));
        }
    }

    private string OptionLabel(IGameClient client, MapVoteOption option)
    {
        if (option.IsExtend)
        {
            return string.Format(Text(client, ChooserTexts.VoteExtend), option.ExtendMinutes);
        }

        return option.Tier > 0 ? $"{option.Map} (T{option.Tier})" : option.Map;
    }

    private static string Duration(float seconds)
    {
        var total = (int) MathF.Max(0, MathF.Ceiling(seconds));

        return $"{total / 60}:{total % 60:00}";
    }
}
