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
using System.Globalization;

namespace Source2Surf.Timer.Utilities;

internal static class SteamIds
{
    // Account 0 of a public individual account; account ids add to it.
    private const ulong IndividualBase = 76561197960265728UL;

    /// <summary>
    ///     A player's SteamID as a command argument: SteamID64, STEAM_X:Y:Z, or [U:1:N] (brackets optional).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> text, out ulong steamId)
    {
        steamId = 0;
        text    = text.Trim();

        if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id64))
        {
            return Account(id64 - IndividualBase, id64 > IndividualBase, out steamId);
        }

        if (text.StartsWith("STEAM_", StringComparison.OrdinalIgnoreCase))
        {
            // STEAM_X:Y:Z: the account is Z * 2 + Y; X (the universe) is 0 or 1 depending on the game.
            var parts = text[6..];

            if (parts.IndexOf(':') is var first and > 0
                && parts[(first + 1)..] is var rest
                && rest.IndexOf(':') is var second and > 0
                && uint.TryParse(rest[..second], NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y <= 1
                && uint.TryParse(rest[(second + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var z))
            {
                return Account(((ulong) z * 2) + y, true, out steamId);
            }

            return false;
        }

        if (text.Length > 1 && text[0] == '[' && text[^1] == ']')
        {
            text = text[1..^1];
        }

        if (text.StartsWith("U:1:", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var account))
        {
            return Account(account, true, out steamId);
        }

        return false;
    }

    private static bool Account(ulong account, bool valid, out ulong steamId)
    {
        valid   = valid && account is > 0 and <= uint.MaxValue;
        steamId = valid ? IndividualBase + account : 0;

        return valid;
    }
}
