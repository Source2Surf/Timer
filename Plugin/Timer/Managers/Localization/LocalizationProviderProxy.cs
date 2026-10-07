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

using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Localization;

/// <summary>
///     The external <see cref="ILocalizationProvider" /> when a module registers one, else none: everyone reads the
///     Timer's English.
/// </summary>
internal sealed class LocalizationProviderProxy : ExternalModuleProxy<ILocalizationProvider>, ILocalizationProvider
{
    public LocalizationProviderProxy(ISharedSystem shared, ILogger<LocalizationProviderProxy> logger)
        : base(shared, new NoTranslations(), logger)
    {
    }

    protected override string Identity     => ILocalizationProvider.Identity;
    protected override string ContractName => "ILocalizationProvider";

    protected override bool InitFallback()
        => true;

    protected override void ShutdownFallback()
    {
    }

    public string? GetText(PlayerSlot slot, string key)
        => Current.GetText(slot, key);

    public object? LocaleOf(PlayerSlot slot)
        => Current.LocaleOf(slot);

    private sealed class NoTranslations : ILocalizationProvider
    {
        public string? GetText(PlayerSlot slot, string key)
            => null;
    }
}
