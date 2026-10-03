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
using Sharp.Shared.Types;

namespace Source2Surf.Timer.Extensions;

internal static class CommandExtension
{
    /// <summary>
    ///     Parse the 1-based argument <paramref name="index"/>; false when it is missing or malformed.
    ///     Use this instead of <see cref="StringCommand.TryGet{T}"/>, which throws when the argument
    ///     is missing and throws <see cref="NotSupportedException"/> unless T is a nullable numeric type.
    /// </summary>
    public static bool TryGetArg<T>(this StringCommand command, int index, out T value)
        where T : IParsable<T>
    {
        if (index >= 1
            && index <= command.ArgCount
            && T.TryParse(command.GetArg(index), CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        value = default!;
        return false;
    }
}
