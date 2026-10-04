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
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Sharp.Shared.GameEntities;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Zone;
using ZLinq;

// ReSharper disable once CheckNamespace
namespace Source2Surf.Timer.Modules;

internal partial class ZoneModule
{
    // Players who ran !zone, which only admins with timer:zone can, until they close the editor or leave.
    private readonly bool[] _zoneEditors = new bool[PlayerSlot.MaxPlayerCount];

    public event Action<PlayerSlot>? EditorRequested;

    public int EditVersion { get; private set; }

    public bool IsZoneEditor(PlayerSlot slot)
        => _zoneEditors[slot];

    public void CloseZoneEditor(PlayerSlot slot)
        => _zoneEditors[slot] = false;

    public IReadOnlyList<ZoneEntry> GetZones()
    {
        var zones = ZoneEntries();
        zones.Sort(ZoneEdit.Compare);

        return zones;
    }

    public int NextZoneNumber(int track, EZoneType type)
        => ZoneEdit.NextNumber(ZoneEntries(), track, type);

    public ZoneBuildState? GetZoneBuild(PlayerSlot slot)
        => _buildZoneInfo[slot] is { } build ? new ZoneBuildState(build.Track, build.Zone, build.Number, build.Step) : null;

    public bool StartZoneBuild(PlayerSlot slot, int track, EZoneType type, int number)
    {
        if (!_zoneEditors[slot]
            || !ZoneEdit.IsValid(track, type, number)
            || _bridge.ClientManager.GetGameClient(slot)?.GetPlayerController()?.GetPlayerPawn() is not
            {
                IsValidEntity: true, IsAlive: true,
            })
        {
            return false;
        }

        ClearBuildZoneInfo(slot);

        var buildInfo = new BuildZoneInfo
        {
            Track  = track,
            Zone   = type,
            Number = number,
        };

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "rendercolor", "255 255 255" },
            { "BoltWidth", "6" },
        };

        if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is { IsValidEntity: true } directionBeam)
        {
            buildInfo.DirectionBeam = directionBeam;
        }

        kv["rendercolor"] = "255 0 0";

        for (var i = 0; i < buildInfo.SnapBeams.Length; i++)
        {
            if (_bridge.EntityManager.SpawnEntitySync<IBaseModelEntity>("env_beam", kv) is { IsValidEntity: true } snapBeam)
            {
                buildInfo.SnapBeams[i] = snapBeam;
            }
        }

        _buildZoneInfo[slot] = buildInfo;
        EditVersion++;

        return true;
    }

    public bool CancelZoneBuild(PlayerSlot slot)
    {
        if (_buildZoneInfo[slot] is null)
        {
            return false;
        }

        ClearBuildZoneInfo(slot);
        EditVersion++;

        return true;
    }

    // Only zones added in game; the map's own zones stay.
    public bool DeleteZone(PlayerSlot slot, uint id)
    {
        if (!_zoneEditors[slot] || !_zones.TryGetValue(id, out var info) || info.Prebuilt)
        {
            return false;
        }

        // Out of the list now: the entity only goes at the end of the frame, after the zones are saved.
        _zones.Remove(id);
        UnindexZone(info);
        RecountStages(info.Track);

        if (info.Beams is { } beams)
        {
            foreach (var beam in beams)
            {
                if (beam is { IsValidEntity: true })
                {
                    beam.Kill();
                }
            }
        }

        if (_bridge.EntityManager.FindEntityByIndex(info.Index) is { IsValidEntity: true } trigger
            && trigger.Handle.GetValue() == id)
        {
            trigger.Kill();
        }

        EditVersion++;
        SaveCustomZones();

        return true;
    }

    private List<ZoneEntry> ZoneEntries()
    {
        var zones = new List<ZoneEntry>(_zones.Count);

        foreach (var (id, info) in _zones)
        {
            zones.Add(new ZoneEntry(id, info.Track, info.ZoneType, info.Data, info.Prebuilt));
        }

        return zones;
    }

    // Like the map's own zones: a bonus with a start zone has at least 1 stage, and the highest stage counts.
    private void RecountStages(int track)
    {
        if (track is < 0 or >= TimerConstants.MAX_TRACK)
        {
            return;
        }

        var stages = track > 0 && HasZone(track, EZoneType.Start) ? 1 : -1;

        foreach (var stage in _zonesByTrackType[track, (int) EZoneType.Stage])
        {
            stages = Math.Max(stages, stage.Data);
        }

        _currentMaxStages[track] = stages;
    }

    private void SaveCustomZones()
    {
        var mapName = _bridge.CurrentMapName;

        var zones = _zones.AsValueEnumerable()
                          .Where(i => !i.Value.Prebuilt)
                          .Select(i => ZoneMapper.ToZoneData(i.Value))
                          .ToArray();

        Task.Run(async () =>
                 {
                     try
                     {
                         await RetryHelper.RetryAsync(() => _requestManager.SaveZonesAsync(mapName, zones),
                                                      RetryHelper.IsTransient,
                                                      _logger,
                                                      "SaveZonesAsync");
                     }
                     catch (Exception e)
                     {
                         _logger.LogError(e, "Failed to save custom zones to database");
                     }
                 },
                 _bridge.CancellationToken);
    }
}
