using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Timer.Backend.Configuration;

/// <summary>
/// Configuration for the optional MagicOnion write API.
/// </summary>
internal sealed class TimerWriteApiOptions
{
    public const string SectionName = "TimerBackend:WriteApi";

    public bool Enabled { get; }

    public int RulesetVersion { get; }

    public IReadOnlyDictionary<int, double> StyleFactors { get; }

    private TimerWriteApiOptions(bool enabled,
                                 int rulesetVersion,
                                 IReadOnlyDictionary<int, double> styleFactors)
    {
        Enabled = enabled;
        RulesetVersion = rulesetVersion;
        StyleFactors = styleFactors;
    }

    public static TimerWriteApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        foreach (var setting in section.GetChildren())
        {
            if (!string.Equals(setting.Key, "Enabled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "RulesetVersion", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "StyleFactors", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{setting.Key} is not supported.");
            }
        }

        var enabled = ParseBoolean(section["Enabled"], $"{SectionName}:Enabled");
        var rulesetVersion = string.IsNullOrWhiteSpace(section["RulesetVersion"])
                                 ? (enabled ? 1 : 0)
                                 : ParseInteger(section["RulesetVersion"],
                                                $"{SectionName}:RulesetVersion",
                                                minimum: 0);
        var styleFactors = ParseStyleFactors(section.GetSection("StyleFactors"));

        // A fresh backend accepts the main style at factor 1 without additional setup.
        // Once custom factors are configured, style 0 must remain explicit.
        if (styleFactors.Count == 0)
        {
            styleFactors.Add(0, 1d);
        }

        if (enabled)
        {
            if (rulesetVersion <= 0)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:RulesetVersion must be greater than zero when the write API is enabled.");
            }

            if (!styleFactors.ContainsKey(0))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors must contain style 0 when the write API is enabled.");
            }
        }

        return new TimerWriteApiOptions(
            enabled,
            rulesetVersion,
            new ReadOnlyDictionary<int, double>(styleFactors));
    }

    private static Dictionary<int, double> ParseStyleFactors(IConfigurationSection section)
    {
        var styleFactors = new Dictionary<int, double>();

        foreach (var child in section.GetChildren())
        {
            var styleKey = child.Key.Trim();
            if (!int.TryParse(styleKey,
                              NumberStyles.Integer,
                              CultureInfo.InvariantCulture,
                              out var style)
                || style is < 0 or > 15)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors:{child.Key} must be an integer from 0 through 15.");
            }

            if (!double.TryParse(child.Value,
                                 NumberStyles.Float,
                                 CultureInfo.InvariantCulture,
                                 out var factor)
                || !double.IsFinite(factor)
                || factor is < 0 or > 100)
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors:{child.Key} must be a finite number from 0 through 100.");
            }

            if (!styleFactors.TryAdd(style, factor))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:StyleFactors contains duplicate style '{style}'.");
            }
        }

        return styleFactors;
    }

    private static bool ParseBoolean(string? raw, string path)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!bool.TryParse(raw, out var value))
        {
            throw new InvalidOperationException($"{path} must be true or false.");
        }

        return value;
    }

    private static int ParseInteger(string? raw, string path, int minimum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        if (!int.TryParse(raw,
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out var value)
            || value < minimum)
        {
            throw new InvalidOperationException($"{path} must be an integer greater than or equal to {minimum}.");
        }

        return value;
    }

}
