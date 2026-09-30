using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

public sealed class ClosestFrameIndexTests
{
    [Fact]
    public void MatchesBruteForceAcrossTreeAndSimdBoundaries()
    {
        var random = new Random(0x5EED);
        int[] sizes = [0, 1, 2, 15, 16, 17, 31, 32, 33, 100, 2048, 2049, 5000];

        foreach (var size in sizes)
        {
            var frames = new ReplayFrameData[size];
            for (var i = 0; i < size; i++)
            {
                frames[i] = i > 0 && i % 11 == 0
                    ? frames[i - 1]
                    : Frame((float) (random.NextDouble() * 10000d - 5000d),
                            (float) (random.NextDouble() * 10000d - 5000d),
                            (float) (random.NextDouble() * 10000d - 5000d));
            }

            var index = new ClosestFrameIndex(frames);
            for (var queryNumber = 0; queryNumber < 24; queryNumber++)
            {
                var query = new Vector((float) (random.NextDouble() * 12000d - 6000d),
                                       (float) (random.NextDouble() * 12000d - 6000d),
                                       (float) (random.NextDouble() * 12000d - 6000d));
                var preferredFrame = random.Next(-size - 1, size * 2 + 2);

                var expectedIndex = FindBruteForce(frames, query, preferredFrame, out var expectedDistance);
                var actualIndex   = index.FindClosest(query, preferredFrame, out var actualDistance);

                Assert.Equal(expectedIndex, actualIndex);
                if (float.IsPositiveInfinity(expectedDistance))
                {
                    Assert.True(float.IsPositiveInfinity(actualDistance));
                }
                else
                {
                    var tolerance = MathF.Max(1e-3f, MathF.Abs(expectedDistance) * 1e-6f);
                    Assert.InRange(MathF.Abs(actualDistance - expectedDistance), 0f, tolerance);
                }
            }
        }
    }

    [Fact]
    public void EqualDistancesPreferTheNearestTimelineFrameThenTheLowerIndex()
    {
        var frames = Enumerable.Range(0, 33)
                               .Select(i => Frame(i * 100f, i * 200f, i * 300f))
                               .ToArray();
        frames[15] = Frame(1f, 2f, 3f);
        frames[17] = Frame(1f, 2f, 3f);

        var index = new ClosestFrameIndex(frames);
        var query = new Vector(1f, 2f, 3f);

        Assert.Equal(17, index.FindClosest(query, 17, out var exactHintDistance));
        Assert.Equal(0f, exactHintDistance);
        Assert.Equal(15, index.FindClosest(query, 16, out var equalOffsetDistance));
        Assert.Equal(0f, equalOffsetDistance);
    }

    [Fact]
    public void IgnoresNonFiniteFramesAndRejectsNonFiniteQueries()
    {
        ReplayFrameData[] frames =
        [
            Frame(float.NaN, 0f, 0f),
            Frame(-1f, 0f, 0f),
            Frame(float.PositiveInfinity, 0f, 0f),
            Frame(4f, 0f, 0f),
        ];
        var index = new ClosestFrameIndex(frames);

        Assert.Equal(1, index.FindClosest(new Vector(0f, 0f, 0f), 3, out var distance));
        Assert.Equal(1f, distance);
        Assert.Equal(-1, index.FindClosest(new Vector(float.NaN, 0f, 0f), 3, out var invalidDistance));
        Assert.True(float.IsPositiveInfinity(invalidDistance));
    }

    [Fact]
    public void ReturnsThePreferredFrameWhenFiniteDistancesOverflow()
    {
        var frames = Enumerable.Range(0, 17)
                               .Select(_ => Frame(float.MaxValue, 0f, 0f))
                               .ToArray();
        var index = new ClosestFrameIndex(frames);

        Assert.Equal(12, index.FindClosest(new Vector(-float.MaxValue, 0f, 0f), 12, out var distance));
        Assert.True(float.IsPositiveInfinity(distance));
    }

    private static int FindBruteForce(IReadOnlyList<ReplayFrameData> frames,
                                      in Vector                     query,
                                      int                           preferredFrame,
                                      out float                     bestDistance)
    {
        bestDistance = float.PositiveInfinity;
        var bestIndex = -1;

        for (var i = 0; i < frames.Count; i++)
        {
            var origin = frames[i].Origin;
            if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            {
                continue;
            }

            var distance = DistanceSquared(origin, query);
            if (distance < bestDistance
                || (distance == bestDistance
                    && (bestIndex < 0
                        || Math.Abs((long) i - preferredFrame) < Math.Abs((long) bestIndex - preferredFrame)
                        || (Math.Abs((long) i - preferredFrame) == Math.Abs((long) bestIndex - preferredFrame)
                            && i < bestIndex))))
            {
                bestDistance = distance;
                bestIndex    = i;
            }
        }

        return bestIndex;
    }

    private static float DistanceSquared(in Vector left, in Vector right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        var dz = left.Z - right.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private static ReplayFrameData Frame(float x, float y, float z)
        => new() { Origin = new Vector(x, y, z) };
}
