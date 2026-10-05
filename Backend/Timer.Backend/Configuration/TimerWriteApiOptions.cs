using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
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

    /// <summary>
    /// The configured factors; without any, those the game servers registered for their styles, or until then the
    /// implicit style-0 default.
    /// </summary>
    public IReadOnlyDictionary<int, double> StyleFactors => Volatile.Read(ref _styleFactors);

    private IReadOnlyDictionary<int, double> _styleFactors;
    private volatile bool                    _hasRegisteredStyleFactors;

    /// <summary>
    /// False when <see cref="StyleFactors"/> is only the implicit style-0 default. Score
    /// administration must not treat that default as the serving instance's policy.
    /// </summary>
    public bool HasExplicitStyleFactors { get; }

    /// <summary>
    /// True once game servers' registered factors replaced the implicit style-0 default.
    /// </summary>
    public bool HasRegisteredStyleFactors => _hasRegisteredStyleFactors;

    /// <summary>
    /// Factors to put on every board a tier change or recalculation queues: configured or registered, with style 0.
    /// </summary>
    public bool HasScorePolicy => (HasExplicitStyleFactors || HasRegisteredStyleFactors) && StyleFactors.ContainsKey(0);

    /// <summary>
    /// Local listener ports that may serve write RPCs. Empty means every Kestrel listener.
    /// </summary>
    public IReadOnlySet<int> LocalPorts { get; }

    private TimerWriteApiOptions(bool enabled,
                                 int rulesetVersion,
                                 IReadOnlyDictionary<int, double> styleFactors,
                                 bool hasExplicitStyleFactors,
                                 IReadOnlySet<int> localPorts)
    {
        Enabled = enabled;
        RulesetVersion = rulesetVersion;
        _styleFactors = styleFactors;
        HasExplicitStyleFactors = hasExplicitStyleFactors;
        LocalPorts = localPorts;
    }

    public static TimerWriteApiOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);
        foreach (var setting in section.GetChildren())
        {
            if (!string.Equals(setting.Key, "Enabled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "RulesetVersion", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "StyleFactors", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(setting.Key, "LocalPorts", StringComparison.OrdinalIgnoreCase))
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
        var hasExplicitStyleFactors = styleFactors.Count != 0;
        var localPorts = ParseLocalPorts(section.GetSection("LocalPorts"));

        // A fresh backend accepts the main style at factor 1 without additional setup.
        // Once custom factors are configured, style 0 must remain explicit.
        if (!hasExplicitStyleFactors)
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
            new ReadOnlyDictionary<int, double>(styleFactors),
            hasExplicitStyleFactors,
            localPorts);
    }

    /// <summary>
    /// Takes the game servers' registered factors in place of the implicit default. Configured factors always win, and
    /// a set without style 0 is refused.
    /// </summary>
    public bool UseRegisteredStyleFactors(IReadOnlyDictionary<int, double> factors)
    {
        ArgumentNullException.ThrowIfNull(factors);

        if (HasExplicitStyleFactors || !factors.ContainsKey(0))
        {
            return false;
        }

        Volatile.Write(ref _styleFactors, new ReadOnlyDictionary<int, double>(new Dictionary<int, double>(factors)));
        _hasRegisteredStyleFactors = true;

        return true;
    }

    private static HashSet<int> ParseLocalPorts(IConfigurationSection section)
    {
        var localPorts = new HashSet<int>();

        // Accept a single scalar (environment variable) or a JSON array, but not both: configuration
        // layers merge rather than replace, so an env scalar on top of an appsettings array would
        // silently keep the old ports allowed alongside the new one.
        var rawPorts = new List<(string Path, string? Value)>();
        var children = section.GetChildren().ToList();
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            if (children.Count != 0)
            {
                throw new InvalidOperationException(
                    $"{section.Path} is set both as a single value and as a list (e.g. an environment variable over an appsettings array). Use one form; for a list in environment variables use {section.Path.Replace(":", "__")}__0, __1, ...");
            }

            rawPorts.Add((section.Path, section.Value));
        }

        foreach (var child in children)
        {
            rawPorts.Add((child.Path, child.Value));
        }

        foreach (var (path, value) in rawPorts)
        {
            if (!int.TryParse(value,
                              NumberStyles.None,
                              CultureInfo.InvariantCulture,
                              out var port)
                || port is < 1 or > 65535)
            {
                throw new InvalidOperationException($"{path} must be a port number from 1 through 65535.");
            }

            localPorts.Add(port);
        }

        return localPorts;
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
