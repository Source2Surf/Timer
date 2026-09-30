using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Benchmarks.Workload;

public enum QueryScenario
{
    OnPath,
    Random,
}

internal readonly record struct ReplayQuery(Vector Position, int PreferredFrame);

internal static class ReplayWorkload
{
    public const int QueryCount = 4096;

    public static ReplayFrameData[] CreateFrames(int count)
    {
        var frames = new ReplayFrameData[count];
        for (var i = 0; i < count; i++)
        {
            var t = i * 0.0078125f;
            var origin = new Vector(
                (i * 1.25f) + (MathF.Sin(t * 0.19f) * 640f),
                (MathF.Sin(t) * 2048f) + (MathF.Sin(t * 0.071f) * 512f),
                (MathF.Cos(t * 0.43f) * 768f) + (MathF.Sin(t * 0.013f) * 256f));

            // Short stationary stretches exercise the equal-position behavior present around
            // starts, checkpoints and pauses without dominating the trajectory.
            if (i > 0 && i % 4096 is >= 2048 and < 2052)
            {
                origin = frames[i - 1].Origin;
            }

            frames[i] = new ReplayFrameData { Origin = origin };
        }

        return frames;
    }

    public static ReplayQuery[] CreateQueries(ReplayFrameData[] frames, QueryScenario scenario)
    {
        var queries = new ReplayQuery[QueryCount];
        var random  = new Random(0x5EED + frames.Length + (int) scenario);

        for (var i = 0; i < queries.Length; i++)
        {
            if (scenario == QueryScenario.OnPath)
            {
                const int segmentLength = QueryCount / 8;
                var segment    = i / segmentLength;
                var localIndex = i % segmentLength;
                var frameIndex = ((segment * frames.Length / 8) + (localIndex * 7)) % frames.Length;
                var origin     = frames[frameIndex].Origin;
                var phase      = i * 0.03125f;
                var position   = new Vector(origin.X + (MathF.Sin(phase) * 12f),
                                            origin.Y + (MathF.Cos(phase * 0.7f) * 18f),
                                            origin.Z + (MathF.Sin(phase * 0.3f) * 8f));
                queries[i] = new ReplayQuery(position, frameIndex);
                continue;
            }

            var randomPosition = new Vector(
                (float) (random.NextDouble() * (frames.Length * 1.25d + 4096d) - 2048d),
                (float) (random.NextDouble() * 6144d - 3072d),
                (float) (random.NextDouble() * 2560d - 1280d));
            queries[i] = new ReplayQuery(randomPosition, random.Next(frames.Length));
        }

        return queries;
    }
}
