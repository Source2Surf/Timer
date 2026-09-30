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

using Sharp.Shared;
using Sharp.Shared.Definition;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;

namespace Source2Surf.Timer.Extensions;

internal static class ChatExtension
{
    private const string Tag = $" {ChatColor.Lime}Timer{ChatColor.White} | ";

    public static void PrintToChat(this IPlayerController controller, string msg)
        => controller.Print(HudPrintChannel.Chat, $"{Tag}{msg}");

    public static void PrintToChat(this IPlayerPawn pawn, string msg)
        => pawn.Print(HudPrintChannel.Chat, $"{Tag}{msg}");

    public static void PrintToChatWithPrefix(this IModSharp sharp, string msg)
        => sharp.PrintToChatAll($"{Tag}{msg}");
}
