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
using System.Diagnostics.CodeAnalysis;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A text made from a few values, kept while they stay the same, so a refresh doesn't format a string equal to the
///     last one again. The values should take in the reader's TrEpoch when the text is translated. Kept as a field of
///     its owner, never copied.
/// </summary>
internal struct HudMemo<TKey> where TKey : IEquatable<TKey>
{
    private TKey    _key;
    private string? _text;

    public bool TryGet(TKey key, [NotNullWhen(true)] out string? text)
    {
        if (_text is not null && _key.Equals(key))
        {
            text = _text;

            return true;
        }

        text = null;

        return false;
    }

    public string Set(TKey key, string text)
    {
        _key  = key;
        _text = text;

        return text;
    }
}
