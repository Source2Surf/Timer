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

namespace Source2Surf.Timer.Managers.Permission;

/// <summary>
///     The external <see cref="IPermissionProvider" /> when a module registers one, else nobody has any permission.
/// </summary>
internal sealed class PermissionProviderProxy : ExternalModuleProxy<IPermissionProvider>, IPermissionProvider
{
    public PermissionProviderProxy(ISharedSystem shared, ILogger<PermissionProviderProxy> logger)
        : base(shared, new NoPermissions(), logger)
    {
    }

    protected override string Identity     => IPermissionProvider.Identity;
    protected override string ContractName => "IPermissionProvider";

    protected override bool InitFallback()
        => true;

    protected override void ShutdownFallback()
    {
    }

    public bool HasPermission(SteamID steamId, string permission)
        => Current.HasPermission(steamId, permission);

    private sealed class NoPermissions : IPermissionProvider
    {
        public bool HasPermission(SteamID steamId, string permission)
            => false;
    }
}
