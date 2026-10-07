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
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
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
    ///     A message to every player, each in their own language: made once per language.
    /// </summary>
    public static void PrintToChatAll(this IClientManager clients, ILocalizationProvider localization, Func<ChatTr, string> message)
    {
        Dictionary<object, string>? byLocale = null;
        string?                     unknown  = null; // without a provider everyone reads the same

        foreach (var client in clients.GetGameClients(true))
        {
            if (client.IsFakeClient || client.IsHltv)
            {
                continue;
            }

            var    slot = client.Slot;
            string text;

            if (localization.LocaleOf(slot) is not { } locale)
            {
                text = unknown ??= Prefix + message(localization.For(slot));
            }
            else
            {
                byLocale ??= [];

                if (!byLocale.TryGetValue(locale, out text!))
                {
                    byLocale[locale] = text = Prefix + message(localization.For(slot));
                }
            }

            client.Print(HudPrintChannel.Chat, text);
        }
    }
}
