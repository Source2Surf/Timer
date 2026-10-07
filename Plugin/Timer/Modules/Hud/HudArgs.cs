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
using Cysharp.Text;

namespace Source2Surf.Timer.Modules.Hud;

/// <summary>
///     A value a HUD text's {n} takes, written straight into the text being built: no string of its own. (ZString
///     formats other types through ToString.)
/// </summary>
internal interface IHudArg
{
    void AppendTo(ref Utf16ValueStringBuilder sb);
}

internal readonly struct TextArg(string value) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
        => sb.Append(value);
}

internal readonly struct IntArg(int value) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
        => sb.Append(value);
}

/// <summary>
///     A time as <see cref="HudFormat.FormatTime" /> writes it ("5.123", "01:05.500"), then <paramref name="after" />.
/// </summary>
internal readonly struct TimeArg(float seconds, string? after = null) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
        Utils.FormatTime(ref sb, seconds > 0 ? seconds : 0, true);

        if (after is not null)
        {
            sb.Append(after);
        }
    }
}

/// <summary>
///     A difference as <see cref="HudFormat.FormatDiff" /> writes it: "+1.500" / "-0.400".
/// </summary>
internal readonly struct DiffArg(long ms) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
        sb.Append(ms < 0 ? '-' : '+');
        Utils.AppendMillis(ref sb, Math.Abs(ms));
    }
}

/// <summary>
///     Two decimals, the same everywhere: "93.40".
/// </summary>
internal readonly struct Fixed2Arg(float value) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
        Span<char> text = stackalloc char[32];

        if (value.TryFormat(text, out var written, "F2", CultureInfo.InvariantCulture))
        {
            sb.Append(text[..written]);
        }
    }
}

/// <summary>
///     A speed difference as <see cref="HudFormat.FormatSpeedDiff" /> writes it: "+12 u/s" / "-9 u/s".
/// </summary>
internal readonly struct SpeedDiffArg(float difference) : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
        var n = HudFormat.RoundSpeed(difference);
        sb.Append(n < 0 ? '-' : '+');
        sb.Append(Math.Abs(n));
        sb.Append(" u/s");
    }
}

/// <summary>
///     A name that's either a text of its own or a numbered one ("Stage 3"), written in place.
/// </summary>
internal readonly struct NameArg : IHudArg
{
    private readonly HudTr    _tr;
    private readonly HudText? _numbered;
    private readonly int      _number;
    private readonly string?  _plain;

    public NameArg(string plain)
        => (_tr, _numbered, _number, _plain) = (default, null, 0, plain);

    public NameArg(HudTr tr, HudText numbered, int number)
        => (_tr, _numbered, _number, _plain) = (tr, numbered, number, null);

    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
        if (_plain is not null)
        {
            sb.Append(_plain);
        }
        else
        {
            _tr.Format(ref sb, _numbered!, new IntArg(_number));
        }
    }
}

internal readonly struct NoArg : IHudArg
{
    public void AppendTo(ref Utf16ValueStringBuilder sb)
    {
    }
}

internal static class HudTemplate
{
    /// <summary>
    ///     Writes <paramref name="template" /> with {0} and {1} filled ("{{" and "}}" are braces). False for a
    ///     placeholder there's no value for, or a stray brace: the caller falls back.
    /// </summary>
    public static bool TryAppend<T1, T2>(ref Utf16ValueStringBuilder sb, string template, in T1 a, in T2 b, int args)
        where T1 : struct, IHudArg
        where T2 : struct, IHudArg
    {
        var rest = template.AsSpan();

        while (rest.Length > 0)
        {
            var at = rest.IndexOfAny('{', '}');

            if (at < 0)
            {
                sb.Append(rest);

                return true;
            }

            sb.Append(rest[..at]);

            if (at + 1 < rest.Length && rest[at + 1] == rest[at])
            {
                sb.Append(rest[at]);
                rest = rest[(at + 2)..];

                continue;
            }

            if (rest[at] == '}')
            {
                return false;
            }

            var close = rest[at..].IndexOf('}');

            if (close < 2
                || !int.TryParse(rest.Slice(at + 1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                || index >= args)
            {
                return false;
            }

            if (index == 0)
            {
                a.AppendTo(ref sb);
            }
            else
            {
                b.AppendTo(ref sb);
            }

            rest = rest[(at + close + 1)..];
        }

        return true;
    }
}
