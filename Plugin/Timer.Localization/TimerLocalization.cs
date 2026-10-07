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
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Modules.LocalizerManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Timer.Localization;

/// <summary>
///     Connects the Timer's <see cref="ILocalizationProvider" /> to ModSharp's LocalizerManager, so each player reads
///     the Timer in their game language. The texts are in sharp/locales/surftimer.json, which this module keeps the
///     same as the one it ships with; a server's own changes and languages go in surftimer.custom.json, which is read
///     after it and wins.
/// </summary>
public sealed class TimerLocalization : IModSharpModule, ILocalizationProvider
{
    private const string LocaleName              = "surftimer";
    private const string CustomLocaleName        = "surftimer.custom";
    private const string LocalizerManagerLibrary = "Sharp.Modules.LocalizerManager";

    // LocalizerManager only hands out formatted texts. Formatted with its own placeholders as the values, a
    // template comes back as it is, so the Timer can fill it in on every refresh without a lookup each time.
    private static readonly object?[] Placeholders = ["{0}", "{1}", "{2}", "{3}", "{4}", "{5}"];

    private readonly ISharedSystem               _shared;
    private readonly ILogger<TimerLocalization>  _logger;
    private readonly string                      _bundledLocale;
    private readonly string                      _installedLocale;
    private readonly string                      _customLocale;

    // By the player's locale, which LocalizerManager replaces when their language changes or the files are
    // reloaded, so a stale text is never read. A missing text is cached as null too.
    private readonly ConditionalWeakTable<ILocale, Dictionary<string, string?>> _texts = new ();

    private IModSharpModuleInterface<ILocalizerManager>? _localizer;

    public TimerLocalization(ISharedSystem  sharedSystem,
                             string         dllPath,
                             string         sharpPath,
                             Version        version,
                             IConfiguration configuration,
                             bool           hotReload)
    {
        _shared = sharedSystem;
        _logger = sharedSystem.GetLoggerFactory().CreateLogger<TimerLocalization>();

        var moduleDirectory = File.Exists(dllPath) ? Path.GetDirectoryName(dllPath)! : dllPath;
        _bundledLocale   = Path.Combine(moduleDirectory, "locales", $"{LocaleName}.json");
        _installedLocale = Path.Combine(sharpPath, "locales", $"{LocaleName}.json");
        _customLocale    = Path.Combine(sharpPath, "locales", $"{CustomLocaleName}.json");
    }

    public string DisplayName   => "SurfTimer Localization";
    public string DisplayAuthor => "github.com/Nukoooo";

    public bool Init()
        => true;

    public void PostInit()
    {
        InstallLocale();
        Connect();
        _shared.GetSharpModuleManager().RegisterSharpModuleInterface<ILocalizationProvider>(this, ILocalizationProvider.Identity, this);
    }

    public void OnLibraryConnected(string name)
    {
        if (name.Equals(LocalizerManagerLibrary, StringComparison.OrdinalIgnoreCase))
        {
            Connect();
        }
    }

    public void OnLibraryDisconnect(string name)
    {
        if (name.Equals(LocalizerManagerLibrary, StringComparison.OrdinalIgnoreCase))
        {
            _localizer = null;
            _texts.Clear();
        }
    }

    public void OnAllModulesLoaded()
        => Connect();

    public void Shutdown()
        => _texts.Clear();

    public object? LocaleOf(PlayerSlot slot)
        => _localizer?.Instance is { } manager && _shared.GetClientManager().GetGameClient(slot) is { } client
            ? manager.For(client).Culture.Name
            : null;

    public string? GetText(PlayerSlot slot, string key)
    {
        if (_localizer?.Instance is not { } manager || _shared.GetClientManager().GetGameClient(slot) is not { } client)
        {
            return null;
        }

        var locale = manager.For(client);
        var texts  = _texts.GetOrCreateValue(locale);

        if (texts.TryGetValue(key, out var text))
        {
            return text;
        }

        ReadOnlySpan<object?> placeholders = Placeholders;
        text       = locale.TryText(key, out var template, placeholders) ? template : null;
        texts[key] = text;

        return text;
    }

    private void Connect()
    {
        if (_localizer?.Instance is not null)
        {
            return;
        }

        _localizer = _shared.GetSharpModuleManager().GetOptionalSharpModuleInterface<ILocalizerManager>(ILocalizerManager.Identity);

        if (_localizer?.Instance is not { } manager)
        {
            return;
        }

        try
        {
            manager.LoadLocaleFile(LocaleName, true);

            if (File.Exists(_customLocale))
            {
                manager.LoadLocaleFile(CustomLocaleName, true);
            }

            _texts.Clear();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to load the locale file {Name}.json; players read the Timer's English.", LocaleName);
        }
    }

    // sharp/locales/surftimer.json follows the module's, so an update's new texts arrive with it.
    private void InstallLocale()
    {
        try
        {
            if (!File.Exists(_bundledLocale)
                || (File.Exists(_installedLocale) && File.ReadAllBytes(_installedLocale).AsSpan().SequenceEqual(File.ReadAllBytes(_bundledLocale))))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_installedLocale)!);
            File.Copy(_bundledLocale, _installedLocale, true);
            _logger.LogInformation("Updated {Name}.json in {Directory}; put your own changes in {Custom}.json.",
                                   LocaleName,
                                   Path.GetDirectoryName(_installedLocale),
                                   CustomLocaleName);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to install {Name}.json.", LocaleName);
        }
    }
}
