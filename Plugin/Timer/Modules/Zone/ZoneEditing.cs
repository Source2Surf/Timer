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
using Sharp.Shared.Enums;
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

    // By slot: the zone added in game each editor moves and turns, 0 for none.
    private readonly uint[] _editTargets = new uint[PlayerSlot.MaxPlayerCount];

    // Saves run one after another, so the last change made is the one stored.
    private Task _saving = Task.CompletedTask;

    // 开图代次: 上一张图晚到的区域加载结果丢掉
    private int  _zoneLoadGeneration;
    private bool _customZonesLoaded;

    // 回合重启删掉、等帧末重建的自建区域; 这期间整图保存会把它们丢掉
    private readonly List<ZoneInfo> _lostZones;

    public event Action<PlayerSlot>? EditorRequested;

    public int EditVersion { get; private set; }

    public bool CanTurnZones => _canTurn;

    public bool CanEditCustomZones => _customZonesLoaded && _lostZones.Count == 0;

    public bool IsZoneEditor(PlayerSlot slot)
        => _zoneEditors[slot];

    public void CloseZoneEditor(PlayerSlot slot)
    {
        _zoneEditors[slot] = false;
        _editTargets[slot] = 0;
    }

    public uint GetEditTarget(PlayerSlot slot)
        => _editTargets[slot];

    public bool SelectZone(PlayerSlot slot, uint id)
    {
        if (!_zoneEditors[slot] || !_zones.TryGetValue(id, out var info) || info.Prebuilt)
        {
            return false;
        }

        _editTargets[slot] = id;
        EditVersion++;

        return true;
    }

    public void DeselectZone(PlayerSlot slot)
    {
        if (_editTargets[slot] != 0)
        {
            _editTargets[slot] = 0;
            EditVersion++;
        }
    }

    // At the player's middle: a placed zone's box starts a little above the floor it was aimed at.
    public uint ZoneAt(PlayerSlot slot)
    {
        if (_bridge.ClientManager.GetGameClient(slot)?.GetPlayerController()?.GetPlayerPawn() is not { IsValidEntity: true, IsAlive: true } pawn)
        {
            return 0;
        }

        var point = pawn.GetAbsOrigin() + new Vector(0, 0, 36);

        foreach (var (id, info) in _zones)
        {
            if (!info.Prebuilt && ZoneEdit.Contains(info.Corner1, info.Corner2, YawOf(info), point))
            {
                return id;
            }
        }

        return 0;
    }

    public ZonePlacement? GetPlacement(uint id)
        => _zones.TryGetValue(id, out var info) && !info.Prebuilt
            ? new ZonePlacement((info.Corner1 + info.Corner2) / 2f, YawOf(info))
            : null;

    public bool MoveZone(PlayerSlot slot, uint id, Vector delta)
        => Reshape(slot, id, delta, 0f);

    public bool TurnZone(PlayerSlot slot, uint id, float degrees)
        => _canTurn && Reshape(slot, id, new Vector(), degrees);

    // Moves the zone's box, then turns it about its new centre; its teleport point and view go along. The trigger is
    // moved and turned in place, so whoever stands in it touches it as before where it still covers them.
    private bool Reshape(PlayerSlot slot, uint id, Vector delta, float degrees)
    {
        if (!_zoneEditors[slot]
            || !CanEditCustomZones
            || !_zones.TryGetValue(id, out var info)
            || info.Prebuilt
            || _bridge.EntityManager.FindEntityByIndex(info.Index) is not { IsValidEntity: true } trigger
            || trigger.Handle.GetValue() != id)
        {
            return false;
        }

        info.Corner1 += delta;
        info.Corner2 += delta;
        info.Yaw     =  ZoneEdit.NormalizeYaw(info.Yaw + degrees);

        var center = (info.Corner1 + info.Corner2) / 2f;

        if (info.TeleportOrigin is { } teleport)
        {
            info.TeleportOrigin = ZoneEdit.Turn(teleport + delta, center, degrees);
        }

        if (info.TeleportAngles is { } view && degrees != 0f)
        {
            info.TeleportAngles = new Vector(view.X, ZoneEdit.NormalizeYaw(view.Y + degrees), view.Z);
        }

        info.Origin       = TriggerOrigin(info);
        info.GroundOrigin = null;
        info.Edges        = null;
        info.FlatEdges    = null;

        trigger.Teleport(info.Origin, new Vector(0, YawOf(info), 0), null);

        _editTargets[slot] = id;
        ZonesChanged();
        EditVersion++;
        SaveCustomZones();

        return true;
    }

    // Moves point 1 or 2 of the zone's box along one of the box's own axes, as bhoptimer's zone adjuster does; the other
    // point's side stays. The trigger takes the new box in place, its physics built again (SetShape), which needs what
    // turning a zone does. The teleport point stays.
    public bool ResizeZone(PlayerSlot slot, uint id, int point, int axis, float by)
    {
        if (!_canTurn
            || !_zoneEditors[slot]
            || !_zones.TryGetValue(id, out var info)
            || info.Prebuilt
            || _bridge.EntityManager.FindEntityByIndex<IBaseModelEntity>(info.Index) is not { IsValidEntity: true } trigger
            || trigger.Handle.GetValue() != id
            || ZoneEdit.Resize(info.Corner1, info.Corner2, info.Yaw, point, axis, by) is not { } resized)
        {
            return false;
        }

        info.Corner1      = resized.Corner1;
        info.Corner2      = resized.Corner2;
        info.Origin       = TriggerOrigin(info);
        info.GroundOrigin = null;
        info.Edges        = null;
        info.FlatEdges    = null;

        var (mins, maxs) = TriggerBounds(info);
        trigger.SetCollisionBounds(mins, maxs);
        SetShape(trigger, SolidType.Obb);
        trigger.Teleport(info.Origin, new Vector(0, info.Yaw, 0), null);

        _editTargets[slot] = id;
        ZonesChanged();
        EditVersion++;
        SaveCustomZones();

        return true;
    }

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
            || !CanEditCustomZones
            || !ZoneEdit.IsValid(track, type, number)
            || _bridge.ClientManager.GetGameClient(slot)?.GetPlayerController()?.GetPlayerPawn() is not
            {
                IsValidEntity: true, IsAlive: true,
            })
        {
            return false;
        }

        ClearBuildZoneInfo(slot);

        _buildZoneInfo[slot] = new BuildZoneInfo
        {
            Track  = track,
            Zone   = type,
            Number = number,
            Lines  = new BuildLines(_bridge.ModSharp, slot, ZoneLineId),
        };
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
        if (!_zoneEditors[slot] || !CanEditCustomZones || !_zones.TryGetValue(id, out var info) || info.Prebuilt)
        {
            return false;
        }

        // Out of the list now: the entity only goes at the end of the frame, after the zones are saved.
        _zones.Remove(id);
        UnindexZone(info);
        RecountStages(info.Track);
        NotifyZoneRemoved(info);

        if (_bridge.EntityManager.FindEntityByIndex(info.Index) is { IsValidEntity: true } trigger
            && trigger.Handle.GetValue() == id)
        {
            trigger.Kill();
        }

        for (var i = 0; i < _editTargets.Length; i++)
        {
            if (_editTargets[i] == id)
            {
                _editTargets[i] = 0;
            }
        }

        EditVersion++;
        SaveCustomZones();

        return true;
    }

    public bool TeleportToZone(PlayerSlot slot, uint id)
    {
        if (!_zoneEditors[slot]
            || !_zones.TryGetValue(id, out var info)
            || _bridge.ClientManager.GetGameClient(slot)?.GetPlayerController()?.GetPlayerPawn() is not
            {
                IsValidEntity: true, IsAlive: true,
            } pawn)
        {
            return false;
        }

        _timerModule.StopTimer(slot);
        _timerModule.SuppressZoneStarts(slot);
        pawn.Teleport(GetTeleportPosition(info, pawn), null, new Vector());

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

    // Each save waits for the one before, so a burst of edits can't land out of order.
    private void SaveCustomZones()
    {
        var mapName = _bridge.CurrentMapName;

        var zones = _zones.AsValueEnumerable()
                          .Where(i => !i.Value.Prebuilt)
                          .Select(i => ZoneMapper.ToZoneData(i.Value))
                          .ToArray();

        _saving = _saving.ContinueWith(async _ =>
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
                                       _bridge.CancellationToken,
                                       TaskContinuationOptions.None,
                                       TaskScheduler.Default)
                         .Unwrap();
    }
}
