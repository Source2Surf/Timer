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

using Sharp.Shared.GameEntities;
using Sharp.Shared.Objects;

// ReSharper disable CheckNamespace
namespace Source2Surf.Timer.Modules;
// ReSharper restore CheckNamespace

// A radio or chat wheel command only goes through while one of the pawn's m_flRadioTokenSlots is free (negative) or
// older than sv_radio_throttle_window. Taking them for good blocks it; spawning frees them, so it's redone after.
internal partial class MiscModule
{
    private const float RadioSlotTaken = float.MaxValue;
    private const float RadioSlotFree  = -1f;

    private void OnBlockRadioChanged(IConVar conVar)
    {
        var blocked = conVar.GetBool();

        foreach (var client in _bridge.ClientManager.GetGameClients(true))
        {
            if (client.GetPlayerController()?.GetPlayerPawn() is { IsValidEntity: true } pawn)
            {
                SetRadioBlocked(pawn, blocked);
            }
        }
    }

    private static void SetRadioBlocked(IPlayerPawn pawn, bool blocked)
    {
        if (pawn.GetRadioService() is not { } radio)
        {
            return;
        }

        var slots = radio.GetRadioTokenSlots();

        for (var i = 0; i < slots.Size; i++)
        {
            slots[i] = blocked ? RadioSlotTaken : RadioSlotFree;
        }
    }
}
