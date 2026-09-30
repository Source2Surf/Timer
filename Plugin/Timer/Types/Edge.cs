using System;
using Sharp.Shared.Types;

namespace Source2Surf.Timer.Types;

public readonly struct Edge : IEquatable<Edge>
{
    public readonly Vector V1;
    public readonly Vector V2;

    public Edge(Vector a, Vector b)
    {
        // Snap vertices to 2 decimal places to fix engine floating-point drift
        var sa = new Vector(MathF.Round(a.X, 2), MathF.Round(a.Y, 2), MathF.Round(a.Z, 2));
        var sb = new Vector(MathF.Round(b.X, 2), MathF.Round(b.Y, 2), MathF.Round(b.Z, 2));

        // Deterministic sort based on exact snapped values
        bool isAFirst = sa.X < sb.X                   ||
                        (sa.X == sb.X && sa.Y < sb.Y) ||
                        (sa.X == sb.X && sa.Y == sb.Y && sa.Z < sb.Z);

        if (isAFirst)
        {
            V1 = sa;
            V2 = sb;
        }
        else
        {
            V1 = sb;
            V2 = sa;
        }
    }

    public bool Equals(Edge other)
        => V1.X == other.V1.X && V1.Y == other.V1.Y && V1.Z == other.V1.Z &&
           V2.X == other.V2.X && V2.Y == other.V2.Y && V2.Z == other.V2.Z;

    public override int GetHashCode()
        => HashCode.Combine(V1.X, V1.Y, V1.Z, V2.X, V2.Y, V2.Z);
}