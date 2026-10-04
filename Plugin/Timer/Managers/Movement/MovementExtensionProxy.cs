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
using Microsoft.Extensions.Logging;
using Sharp.Shared;
using Sharp.Shared.Units;
using Source2Surf.Timer.Shared.Interfaces;

namespace Source2Surf.Timer.Managers.Movement;

/// <summary>
///     The movement extension module when one is loaded, else the game's own movement.
/// </summary>
internal sealed class MovementExtensionProxy : ExternalModuleProxy<IMovementExtension>, IMovementExtension
{
    public MovementExtensionProxy(ISharedSystem shared, ILogger<MovementExtensionProxy> logger)
        : base(shared, new NoMovementExtension(), logger)
    {
    }

    protected override string Identity     => IMovementExtension.Identity;
    protected override string ContractName => "IMovementExtension";

    protected override bool InitFallback()
        => true;

    protected override void ShutdownFallback()
    {
    }

    public long GetAccelerationStep(PlayerSlot slot, nint moveData)
        => Current.GetAccelerationStep(slot, moveData);

    // Any module may provide it. ModSharp names a module by its assembly and drops its interfaces only after this.
    public void OnModuleUnloading(string module)
    {
        if (Current is not NoMovementExtension
            && string.Equals(Current.GetType().Assembly.GetName().Name, module, StringComparison.OrdinalIgnoreCase))
        {
            UseFallback();
        }
    }

    private sealed unsafe class NoMovementExtension : IMovementExtension
    {
        // ReSharper disable once InconsistentNaming
        private const int CMoveData_m_nStartTick_offset = 0xd4;

        public long GetAccelerationStep(PlayerSlot slot, nint moveData)
            => *(int*) (moveData + CMoveData_m_nStartTick_offset);
    }
}
