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
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Extensions;

internal static class ChatExtension
{
    // timer.jsonc's chat.prefix, read as the timer loads.
    public static string Prefix { get; set; } = " " + ChatColorTags.Apply(ChatColorTags.DefaultPrefix);

    public static void PrintToChat(this IPlayerController controller, string msg)
        => controller.Print(HudPrintChannel.Chat, $"{Prefix}{msg}");

    public static void PrintToChat(this IPlayerPawn pawn, string msg)
        => pawn.Print(HudPrintChannel.Chat, $"{Prefix}{msg}");

    public static void PrintToChat(this IGameClient client, string msg)
        => client.Print(HudPrintChannel.Chat, $"{Prefix}{msg}");

    /// <summary>
    ///     The chat texts the player in <paramref name="slot" /> reads.
    /// </summary>
    public static ChatTr For(this ILocalizationProvider localization, PlayerSlot slot)
        => new (localization, slot);

    /// <summary>
    ///     A message to every player, each in their own language: made and sent once per language.
    /// </summary>
    public static void PrintToChatAll(this InterfaceBridge bridge, ILocalizationProvider localization, Func<ChatTr, string> message)
    {
        Dictionary<object, (ulong Players, PlayerSlot First)>? byLocale = null;

        ulong      unknown      = 0; // without a provider everyone reads the same
        PlayerSlot unknownFirst = default;

        foreach (var client in bridge.ClientManager.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            var slot = client.Slot;

            if (localization.LocaleOf(slot) is not { } locale)
            {
                unknownFirst =  unknown == 0 ? slot : unknownFirst;
                unknown      |= 1UL << slot;

                continue;
            }

            byLocale ??= [];
            ref var group = ref CollectionsMarshal.GetValueRefOrAddDefault(byLocale, locale, out var known);

            group.First   =  known ? group.First : slot;
            group.Players |= 1UL << slot;
        }

        if (unknown != 0)
        {
            Send(unknown, unknownFirst);
        }

        if (byLocale is not null)
        {
            foreach (var (players, first) in byLocale.Values)
            {
                Send(players, first);
            }
        }

        void Send(ulong players, PlayerSlot first)
            => bridge.ModSharp.PrintChannelFilter(HudPrintChannel.Chat, Prefix + message(localization.For(first)), new RecipientFilter(players));
    }
}
