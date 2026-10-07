using System;
using Sharp.Shared;
using Sharp.Shared.Types;
using Sharp.Shared.Units;
using Source2Surf.Timer.Utilities;

namespace Source2Surf.Timer.Modules.Zone;

/// <summary>
///     The zone builder's guide lines, drawn for the builder alone with the zone line effect; a line follows its ends.
/// </summary>
internal sealed class BuildLines(IModSharp sharp, PlayerSlot slot, ulong effect)
{
    public const int Direction = 0;
    public const int Snap      = 1; // two lines
    public const int Box       = 3; // twelve edges

    public static readonly Vector White = new (255, 255, 255);
    public static readonly Vector Red   = new (255, 0, 0);

    private static readonly Vector Radius = new (1, 0, 0);

    private const int Count = 15;

    // Out of the way of the server's own particle indices and the other lines'; 0x100 per slot.
    private const uint IndexBase = 0x7D000000;

    private readonly (Vector A, Vector B, Vector Color)?[] _drawn = new (Vector A, Vector B, Vector Color)?[Count];

    private readonly RecipientFilter _to = new (slot);

    private int _calls;

    // Every other user command: smooth enough for a preview, at half the messages.
    public bool Due()
        => (++_calls & 1) == 0;

    // A drawn line only gets what changed.
    public void Draw(int line, Vector a, Vector b, Vector color)
    {
        var index = Index(line);
        var drawn = _drawn[line];

        if (drawn is null)
        {
            Particles.Create(sharp, _to, index, effect);
            Particles.SetPoint(sharp, _to, index, 17, Radius);
        }

        if (drawn is null || !Same(drawn.Value.A, a))
        {
            Particles.SetPoint(sharp, _to, index, 0, a);
        }

        if (drawn is null || !Same(drawn.Value.B, b))
        {
            Particles.SetPoint(sharp, _to, index, 1, b);
        }

        if (drawn is null || !Same(drawn.Value.Color, color))
        {
            Particles.SetPoint(sharp, _to, index, 16, color);
        }

        _drawn[line] = (a, b, color);
    }

    public void DrawBox(in Vector p1, in Vector p2)
    {
        Span<Vector> corners =
        [
            p1,                     // back,  left,  bottom
            new (p1.X, p2.Y, p1.Z), // back,  right, bottom
            new (p2.X, p2.Y, p1.Z), // front, right, bottom
            new (p2.X, p1.Y, p1.Z), // front, left,  bottom

            new (p1.X, p1.Y, p2.Z), // back,  left,  top
            new (p1.X, p2.Y, p2.Z), // back,  right, top
            p2,                     // front, right, top
            new (p2.X, p1.Y, p2.Z), // front, left,  top
        ];

        Span<(int First, int Second)> edges =
        [
            (0, 1), (1, 2), (2, 3), (3, 0), // bottom
            (4, 5), (5, 6), (6, 7), (7, 4), // top
            (0, 4), (1, 5), (2, 6), (3, 7), // sides
        ];

        for (var i = 0; i < edges.Length; i++)
        {
            Draw(Box + i, corners[edges[i].First], corners[edges[i].Second], White);
        }
    }

    public void Clear()
    {
        for (var line = 0; line < Count; line++)
        {
            if (_drawn[line] is not null)
            {
                Particles.Destroy(sharp, _to, Index(line));
                _drawn[line] = null;
            }
        }
    }

    private uint Index(int line)
        => IndexBase + ((uint) (int) slot << 8) + (uint) line;

    private static bool Same(in Vector a, in Vector b)
        => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
}
