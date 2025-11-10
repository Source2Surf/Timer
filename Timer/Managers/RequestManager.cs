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

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Sharp.Shared.Units;
using SurfTimer.Managers.Request.Models;

// ReSharper disable once CheckNamespace
namespace SurfTimer.Managers;

internal enum EAttemptResult
{
    NoNewRecord,
    NewPersonalRecord,
    NewServerRecord,
}

internal interface IRequestManager
{
#region MapInfo

    Task<MapProfile> GetMapInfo(string map);

    Task UpdateMapInfo(MapProfile info);

#endregion

#region Record

    Task<List<RunRecord>> GetMapRecords(Guid mapId);

    Task<List<RunRecord>> GetMapStageRecords(Guid mapId);

    Task<List<RunRecord>> GetMapRecords(Guid mapId, int style, int track);

    Task<List<RunRecord>> GetMapStageRecords(Guid mapId, int style, int track, int stage);

    Task<(EAttemptResult, RunRecord)> AddPlayerRecord(Guid playerId, Guid mapId, RecordRequest recordRequest);

    Task<List<RunRecord>> GetPlayerRecords(Guid playerId, Guid mapId);

    Task<RunRecord?> GetPlayerRecord(Guid playerId, Guid mapId, int style, int track);

    Task<(EAttemptResult, RunRecord)> AddPlayerStageRecord(Guid playerId, Guid mapId, RecordRequest newRunRecord);

    Task<List<RunRecord>> GetPlayerStageRecords(Guid playerId, Guid mapId);

    Task RemoveMapRecords(Guid mapId);

#endregion

    Task<PlayerProfile> GetPlayerProfile(SteamID steamId, string name);
}
