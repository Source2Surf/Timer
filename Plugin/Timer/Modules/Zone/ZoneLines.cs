using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Extensions;
using Source2Surf.Timer.Managers.Localization;
using Source2Surf.Timer.Modules.Zone;
using Source2Surf.Timer.Shared.Interfaces.Modules;
using Source2Surf.Timer.Shared.Models.Zone;
using Source2Surf.Timer.Types;
using Source2Surf.Timer.Utilities;

namespace Source2Surf.Timer.Modules;

// Zones are outlined for each player with our laser line effect, no entities, as timer-zones.jsonc says; !showzones
// turns it off for them. Only the zones of the track the player is on (!r, !b<n>), or of the one they spectate, are
// outlined: another track's zones do nothing for them. A zone editor can have every track's with !zone all. A zone edge
// is one line, shared: it's created and destroyed for every player who gains or drops it with one message each, and
// each look goes once to all who share it; a budget a tick.
internal partial class ZoneModule
{
    // CP0/CP1 the ends, CP16 the colour (0-255), CP17.X the radius.
    private const string ZoneLineEffect = "particles/surftimer_zone_line.vpcf";

    // Out of the way of the server's own particle indices and the builder's.
    private const uint ZoneLineBase = 0x7E000000;

    // Messages a tick for everyone: a creation is five, a removal two.
    private const int LineMessagesPerTick = 200;

    private static readonly ulong ZoneLineId = Particles.ResourceId(ZoneLineEffect);

    private sealed class LineState
    {
        public int Version = -1; // of the zones last drawn; -1 = draw again, -2 = hidden
        public int Track;        // the player's track they were drawn for
    }

    private readonly LineState?[] _lines  = new LineState?[PlayerSlot.MaxPlayerCount];
    private readonly SharedLines  _shared = new ();

    // By slot: who is in the server, so empty slots aren't looked at every tick.
    private ulong _linePlayers;

    private ZoneOutlines _outlines = new ();
    private int          _linesVersion;

    public void OnResourcePrecache()
        => _bridge.ModSharp.PrecacheResource(ZoneLineEffect);

    // Anything that changes what's drawn.
    private void ZonesChanged()
        => _linesVersion++;

    // The client drops its particles with the map.
    private void ForgetLines()
    {
        Array.Clear(_lines);
        _shared.Clear();
    }

    private ECommandAction OnCommandShowZones(PlayerSlot slot, StringCommand command)
    {
        if (!_bridge.TryGetController(slot, out var controller))
        {
            return ECommandAction.Handled;
        }

        var shown = !_settings.ShowsZones(slot);
        _settings.SetShowsZones(slot, shown);
        controller.PrintToChat(_localization.For(slot)[shown ? ChatTexts.ZonesShown : ChatTexts.ZonesHidden]);

        return ECommandAction.Handled;
    }

    public void OnClientPutInServer(PlayerSlot slot)
        => _linePlayers |= 1UL << slot;

    private void DropLines(PlayerSlot slot)
    {
        _lines[slot] =  null;
        _linePlayers &= ~(1UL << slot);
        _shared.Drop(slot);
    }

    // Outlines every track's zones.
    private const int AllTracks = -1;

    // By slot: zone editors who asked for every track's outlines (!zone all).
    private readonly bool[] _allTrackLines = new bool[PlayerSlot.MaxPlayerCount];

    private IReplayModule _replay = null!;

    // Whose track a player's outlines follow: their own, or the player or replay bot they spectate.
    private int OutlinedTrack(PlayerSlot slot)
    {
        if (_allTrackLines[slot] && _zoneEditors[slot])
        {
            return AllTracks;
        }

        if (_bridge.EntityManager.FindPlayerControllerBySlot(slot) is { } controller
            && _bridge.GetObservedSlot(controller) is { } observed
            && observed != slot)
        {
            if (_timerModule.GetTimerInfo(observed) is { } watched)
            {
                return watched.Track;
            }

            // A bot between replays has none (-1): the spectator's own then.
            if (_replay.GetReplayBotData(observed) is { Track: >= 0 } bot)
            {
                return bot.Track;
            }
        }

        return _timerModule.GetTimerInfo(slot)?.Track ?? 0;
    }

    private ECommandAction ToggleAllTrackLines(PlayerSlot slot)
    {
        if (_bridge.TryGetController(slot, out var controller))
        {
            var all = _allTrackLines[slot] = !_allTrackLines[slot];
            controller.PrintToChat(_localization.For(slot)[all ? ChatTexts.ZonesAllTracks : ChatTexts.ZonesOwnTrack]);
        }

        return ECommandAction.Handled;
    }

    // The track each player's outlines follow is looked at every tick; the client only for one whose lines are behind.
    private void OnGameFramePost(bool simulating, bool firstTick, bool lastTick)
    {
        for (var players = _linePlayers; players != 0; players &= players - 1)
        {
            var slot  = (PlayerSlot) System.Numerics.BitOperations.TrailingZeroCount(players);
            var shown = _settings.ShowsZones(slot);
            var track = shown ? OutlinedTrack(slot) : 0;

            if (_lines[slot] is { } settled && settled.Version == (shown ? _linesVersion : -2)
                && (!shown || settled.Track == track))
            {
                continue;
            }

            if (_bridge.ClientManager.GetGameClient(slot) is not { IsFakeClient: false, IsHltv: false } client
                || client.GetPlayerController() is not { ConnectedState: PlayerConnectedState.PlayerConnected })
            {
                continue;
            }

            Redraw(slot, _lines[slot] ??= new LineState(), shown, track);
        }

        SendLines();
    }

    // Marks the lines the player's outlines have now, and unmarks the ones they had.
    private void Redraw(PlayerSlot slot, LineState state, bool shown, int track)
    {
        var mark = _shared.Begin();

        if (shown)
        {
            foreach (var (id, zone) in _zones)
            {
                if ((track != AllTracks && zone.Track != track) || _outlines.For(zone.ZoneType) is not { } outline)
                {
                    continue;
                }

                var edges = zone.Edges ??= GetEdges(zone);

                _shared.Want(slot, mark, id, new LineLook(outline.Color, outline.Width / 2),
                             outline.Flat ? zone.FlatEdges ??= ZoneOutlines.Bottom(edges) : edges);
            }
        }

        _shared.End(slot, mark);

        state.Version = shown ? _linesVersion : -2;
        state.Track   = track;
    }

    private void SendLines()
    {
        var budget = LineMessagesPerTick;

        while (budget > 0 && _shared.TryNext(out var line, out var gone, out var added, out var styles))
        {
            var index = ZoneLineBase + (uint) line.Index;

            if (gone != 0)
            {
                Particles.Destroy(_bridge.ModSharp, new RecipientFilter(gone), index);
                budget -= 2;
            }

            if (added != 0)
            {
                var to = new RecipientFilter(added);
                Particles.Create(_bridge.ModSharp, to, index, ZoneLineId);
                Particles.SetPoint(_bridge.ModSharp, to, index, 0, line.Key.Edge.V1);
                Particles.SetPoint(_bridge.ModSharp, to, index, 1, line.Key.Edge.V2);
                budget -= 3;
            }

            foreach (var (look, players) in styles)
            {
                var to = new RecipientFilter(players);
                Particles.SetPoint(_bridge.ModSharp, to, index, 16, look.Color);
                Particles.SetPoint(_bridge.ModSharp, to, index, 17, new (look.Radius, 0, 0));
                budget -= 2;
            }
        }
    }

    private void LoadZoneOutlines()
    {
        _outlines = ZoneOutlines.Load(Path.Combine(_bridge.SharpPath, "configs", "timer-zones.jsonc"), _logger);
        ZonesChanged();
    }
}

/// <summary>
///     How a zone is outlined for a player: its bottom only or the whole box, the colour (0-255) and the width in units.
/// </summary>
internal readonly record struct ZoneOutline(bool Flat, Vector Color, float Width);

/// <summary>
///     timer-zones.jsonc: how each zone type is outlined. A type without a colour isn't drawn; "flat" types show only
///     their bottom; "width" is in units.
/// </summary>
internal sealed record ZoneOutlines
{
    [JsonPropertyName("colors")]
    public Dictionary<string, int[]> Colors { get; init; } = new (StringComparer.OrdinalIgnoreCase)
    {
        ["start"] = [0, 255, 0],
        ["end"]   = [255, 0, 0],
    };

    [JsonPropertyName("flat")]
    public string[] Flat { get; init; } = [];

    [JsonPropertyName("width")]
    public float Width { get; init; } = 2f;

    public Vector? ColorOf(EZoneType type)
        => Colors.TryGetValue(type.ToString(), out var rgb) && rgb is [var r, var g, var b]
            ? new Vector(Math.Clamp(r, 0, 255), Math.Clamp(g, 0, 255), Math.Clamp(b, 0, 255))
            : null;

    /// <summary>
    ///     A type's outline; null when it isn't drawn.
    /// </summary>
    public ZoneOutline? For(EZoneType type)
        => ColorOf(type) is { } color
            ? new ZoneOutline(Flat.Contains(type.ToString(), StringComparer.OrdinalIgnoreCase), color, Math.Clamp(Width, 0.1f, 16f))
            : null;

    /// <summary>
    ///     The edges along a zone's bottom: those in its lower half that run more across than up.
    /// </summary>
    public static List<Edge> Bottom(List<Edge> edges)
    {
        if (edges.Count == 0)
        {
            return edges;
        }

        var low  = edges.Min(e => MathF.Min(e.V1.Z, e.V2.Z));
        var high = edges.Max(e => MathF.Max(e.V1.Z, e.V2.Z));

        if (high - low < 1f)
        {
            return edges;
        }

        var middle = (low + high) / 2;

        return edges.Where(e => e.V1.Z < middle
                                && e.V2.Z < middle
                                && MathF.Abs(e.V2.Z - e.V1.Z) <= MathF.Sqrt(MathF.Pow(e.V2.X - e.V1.X, 2) + MathF.Pow(e.V2.Y - e.V1.Y, 2)))
                    .ToList();
    }

    public static ZoneOutlines Load(string path, ILogger logger)
    {
        try
        {
            if (File.Exists(path)
                && JsonSerializer.Deserialize<ZoneOutlines>(File.ReadAllText(path), Utils.DeserializerOptions) is { } config)
            {
                // Case-insensitive whatever the file's keys.
                return config with { Colors = new (config.Colors, StringComparer.OrdinalIgnoreCase) };
            }

            var defaults = new ZoneOutlines();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, Utils.SerializerOptions));

            return defaults;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to read {path}, using the default zone outlines", path);

            return new ZoneOutlines();
        }
    }
}
