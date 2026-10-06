using System;
using System.Collections.Frozen;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Sharp.Shared.Definition;

namespace Source2Surf.Timer.Shared;

/// <summary>
///     Turns {green}-style tags, named as in <see cref="ChatColor" /> and in any case, into chat colours. Anything else
///     in braces stays as it is, so format placeholders and {{ }} escapes still work after.
/// </summary>
public static class ChatColorTags
{
    public const string DefaultPrefix = "{lime}Timer{white} | ";

    private static readonly FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> Colors =
        typeof(ChatColor).GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                         .ToFrozenDictionary(f => f.Name, f => (string) f.GetRawConstantValue()!, StringComparer.OrdinalIgnoreCase)
                         .GetAlternateLookup<ReadOnlySpan<char>>();

    public static string Apply(string text)
    {
        var span = text.AsSpan();
        var at   = span.IndexOf('{');

        if (at < 0)
        {
            return text;
        }

        StringBuilder? colored = null;
        var            copied  = 0;

        while (at >= 0)
        {
            var close = span[(at + 1)..].IndexOf('}');

            if (close < 0)
            {
                break;
            }

            var name = span.Slice(at + 1, close);

            if (Colors.TryGetValue(name, out var code))
            {
                colored ??= new StringBuilder(text.Length);
                colored.Append(span[copied..at]).Append(code);
                copied = at + close + 2;
            }

            var next = span[(at + 1)..].IndexOf('{');
            at = next < 0 ? -1 : at + 1 + next;
        }

        return colored is null ? text : colored.Append(span[copied..]).ToString();
    }

    /// <summary>
    ///     What goes before every timer chat message: timer.jsonc's chat.prefix, or <see cref="DefaultPrefix" />, coloured
    ///     and after a space, as chat needs one before a colour.
    /// </summary>
    public static string LoadPrefix(string timerConfigPath)
    {
        var prefix = DefaultPrefix;

        try
        {
            if (File.Exists(timerConfigPath))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(timerConfigPath),
                                                    new () { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("chat", out var chat)
                    && chat.ValueKind == JsonValueKind.Object
                    && chat.TryGetProperty("prefix", out var value)
                    && value.ValueKind == JsonValueKind.String)
                {
                    prefix = value.GetString()!;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // The timer reports a broken timer.jsonc when it loads the rest of it.
        }

        return " " + Apply(prefix);
    }
}
