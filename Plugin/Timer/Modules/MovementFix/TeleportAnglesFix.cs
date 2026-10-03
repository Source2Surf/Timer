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

using Sharp.Shared.Types;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// Keep the player's view through a trigger_teleport. The game turns the velocity along with the view, so it is left
// unturned too by reporting no new velocity.
internal unsafe partial class MovementFixModule
{
    private static bool KeepTeleportAngles(nint entity, Vector* newAngles)
    {
        if (!_keepTeleportAnglesEnabled || entity == nint.Zero || *(nint*) entity != CCSPlayerPawn_vtable)
        {
            return false;
        }

        var slot = GetPlayerSlot(entity);

        if (slot < 0 || _isFakeClient[slot] || !IsAlive(entity))
        {
            return false;
        }

        *newAngles = *(Vector*) (entity + CCSPlayerPawn_m_angEyeAngles_offset);

        return true;
    }
}
