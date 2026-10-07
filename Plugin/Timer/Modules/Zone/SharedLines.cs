using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Types;

namespace Source2Surf.Timer.Modules.Zone;

internal readonly record struct LineKey(uint Zone, Edge Edge);

internal readonly record struct LineLook(Vector Color, float Radius);

/// <summary>
///     Zone lines shared by every player who has them: one particle index per zone edge, created for a mask of players
///     at once. Each client's copy takes its player's look for the zone, sent once to all who share it, so a look change
///     only updates that player's copies. A player's lines are marked between <see cref="Begin" /> and
///     <see cref="End" />; a line whose players changed is queued, and one still queued when more players gain it goes
///     to all of them at once.
/// </summary>
internal sealed class SharedLines
{
    internal sealed class Line(LineKey key, int index)
    {
        public readonly LineKey Key   = key;
        public readonly int     Index = index;

        public ulong Wanted;  // by slot: players whose outlines have it
        public ulong Has;     // by slot: players it was created for
        public ulong Restyle; // by slot: players whose look for it changed
        public int   Mark;
        public bool  Queued;
    }

    private readonly Dictionary<LineKey, Line>    _lines  = [];
    private readonly Queue<Line>                  _queue  = new ();
    private readonly Stack<int>                   _free   = new ();
    private readonly Dictionary<uint, LineLook>?[] _looks  = new Dictionary<uint, LineLook>?[PlayerSlot.MaxPlayerCount]; // by slot, then zone
    private readonly Dictionary<LineLook, ulong>  _styles = [];

    private int _next;
    private int _mark;

    public int Count => _lines.Count;

    public int Begin()
        => ++_mark;

    // The player wants these edges of a zone, in this look.
    public void Want(PlayerSlot slot, int mark, uint zone, LineLook look, List<Edge> edges)
    {
        var bit   = 1UL << slot;
        var looks = _looks[slot] ??= [];

        var restyle = looks.TryGetValue(zone, out var had) && had != look;
        looks[zone] = look;

        foreach (var edge in edges)
        {
            var key = new LineKey(zone, edge);

            if (!_lines.TryGetValue(key, out var line))
            {
                _lines.Add(key, line = new Line(key, _free.TryPop(out var free) ? free : _next++));
            }

            line.Mark = mark;

            if (restyle && (line.Has & bit) != 0)
            {
                line.Restyle |= bit;
                Queue(line);
            }

            Set(line, line.Wanted | bit, line.Has);
        }
    }

    // The player no longer wants what this pass didn't mark.
    public void End(PlayerSlot slot, int mark)
    {
        var bit = 1UL << slot;

        foreach (var line in _lines.Values)
        {
            if (line.Mark != mark && (line.Wanted & bit) != 0)
            {
                Set(line, line.Wanted & ~bit, line.Has);
            }
        }
    }

    // A player who left: their client went with its lines, so nothing is sent for them.
    public void Drop(PlayerSlot slot)
    {
        var bit = 1UL << slot;

        _looks[slot] = null;

        foreach (var line in _lines.Values)
        {
            if (((line.Wanted | line.Has) & bit) != 0)
            {
                line.Restyle &= ~bit;
                Set(line, line.Wanted & ~bit, line.Has & ~bit);
            }
        }
    }

    public void Clear()
    {
        _lines.Clear();
        _queue.Clear();
        _free.Clear();
        System.Array.Clear(_looks);
        _next = 0;
    }

    /// <summary>
    ///     The next queued line, taken as sent: who drops it, who gains it, and the looks to send, each with the players
    ///     who take it (those who gained it and those whose look changed). A line nobody has or wants is let go and its
    ///     index reused.
    /// </summary>
    public bool TryNext(out Line line, out ulong gone, out ulong added, out Dictionary<LineLook, ulong> styles)
    {
        styles = _styles;
        _styles.Clear();

        if (!_queue.TryDequeue(out line!))
        {
            gone = added = 0;

            return false;
        }

        line.Queued = false;
        gone        = line.Has & ~line.Wanted;
        added       = line.Wanted & ~line.Has;

        for (var players = (added | line.Restyle) & line.Wanted; players != 0; players &= players - 1)
        {
            var slot = System.Numerics.BitOperations.TrailingZeroCount(players);

            if (_looks[slot] is { } looks && looks.TryGetValue(line.Key.Zone, out var look))
            {
                CollectionsMarshal.GetValueRefOrAddDefault(_styles, look, out _) |= 1UL << slot;
            }
        }

        line.Has     = line.Wanted;
        line.Restyle = 0;

        if (line.Wanted == 0)
        {
            _lines.Remove(line.Key);
            _free.Push(line.Index);
        }

        return true;
    }

    private void Set(Line line, ulong wanted, ulong has)
    {
        if (line.Wanted == wanted && line.Has == has)
        {
            return;
        }

        line.Wanted = wanted;
        line.Has    = has;
        Queue(line);
    }

    private void Queue(Line line)
    {
        if (!line.Queued)
        {
            line.Queued = true;
            _queue.Enqueue(line);
        }
    }
}
