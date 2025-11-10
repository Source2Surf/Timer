/*
 * Source2Surf/Timer
 * Copyright (C) 2025 Nukoooo
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
 
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

// ReSharper disable once CheckNamespace
namespace SurfTimer.Modules;

internal partial class RecordModule
{
    private ECommandAction OnCommandClearRecords(StringCommand stringCommand)
    {
        _request.RemoveMapRecords(_mapInfo.GetCurrentMapProfile().MapId);

        for (var i = 0; i < Utils.MAX_STYLE; i++)
        {
            for (var j = 0; j < Utils.MAX_TRACK; j++)
            {
                _mapRecordsCache[i, j].Clear();
            }
        }

        for (var style = 0; style < Utils.MAX_STYLE; style++)
        {
            for (var track = 0; track < Utils.MAX_TRACK; track++)
            {
                for (var stage = 0; stage < Utils.MAX_STAGE; stage++)
                {
                    _mapStageRecordsCache[style, track, stage].Clear();
                }
            }
        }

        for (PlayerSlot slot = 0; slot < PlayerSlot.MaxPlayerSlot; slot++)
        {
            ClearPlayerRecord(slot);
        }

        return ECommandAction.Handled;
    }
}