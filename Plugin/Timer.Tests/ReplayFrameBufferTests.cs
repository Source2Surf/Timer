using Sharp.Shared.Types;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;
using Xunit;

namespace Timer.Tests;

// Each test has a pool of its own, so what comes back to it can be counted.
public sealed class ReplayFrameBufferTests
{
    private static ReplayFrameData Frame(int i)
        => new () { Origin = new Vector(i, 0, 0) };

    private static ReplayFrameBuffer Filled(ReplayFrameChunkPool pool, int count, int from = 0)
    {
        var buffer = new ReplayFrameBuffer(pool);

        for (var i = 0; i < count; i++)
        {
            buffer.Add(Frame(from + i));
        }

        return buffer;
    }

    private static void AssertFrames(ReplayFrameBuffer buffer, int from, int count)
    {
        Assert.Equal(count, buffer.Count);

        for (var i = 0; i < count; i++)
        {
            Assert.Equal((float) (from + i), buffer[i].Origin.X);
        }
    }

    [Fact]
    public void FramesRunAcrossChunksInOrder()
    {
        var buffer = Filled(new ReplayFrameChunkPool(16), 2500);

        AssertFrames(buffer, 0, 2500);
        Assert.Equal(3, buffer.ChunkCount);
        Assert.Equal(Enumerable.Range(0, 2500).Select(i => (float) i), buffer.Select(f => f.Origin.X));
    }

    [Fact]
    public void AddRangeFillsAcrossChunks()
    {
        var buffer = Filled(new ReplayFrameChunkPool(16), 1000);

        buffer.AddRange(Enumerable.Range(1000, 1500).Select(Frame).ToArray());

        AssertFrames(buffer, 0, 2500);
        Assert.Equal(3, buffer.ChunkCount);
    }

    [Fact]
    public void DroppingTheFrontGivesBackTheChunksItEmpties()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var buffer = Filled(pool, 2500);

        // 1500..2499 are left, in the second and third chunks.
        buffer.RemoveFirst(1500);

        AssertFrames(buffer, 1500, 1000);
        Assert.Equal(2, buffer.ChunkCount);
        Assert.Equal(1, pool.SpareCount);

        buffer.Add(Frame(2500));
        AssertFrames(buffer, 1500, 1001);
    }

    [Fact]
    public void DroppingEveryFrameFromTheFrontEmptiesIt()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var buffer = Filled(pool, 1500);

        buffer.RemoveFirst(1500);

        Assert.Empty(buffer);
        Assert.Equal(0, buffer.ChunkCount);
        Assert.Equal(2, pool.SpareCount);

        buffer.Add(Frame(7));
        AssertFrames(buffer, 7, 1);
    }

    [Fact]
    public void TruncatingGivesBackTheChunksPastTheEnd()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var buffer = Filled(pool, 2500);

        // 100..1099 are left: the rest of the first chunk and the start of the second.
        buffer.RemoveFirst(100);
        buffer.Truncate(1000);

        AssertFrames(buffer, 100, 1000);
        Assert.Equal(2, buffer.ChunkCount);
        Assert.Equal(1, pool.SpareCount);

        buffer.Truncate(0);

        Assert.Empty(buffer);
        Assert.Equal(3, pool.SpareCount);
    }

    [Fact]
    public void DetachHandsOverTheChunksAndKeepsACopyOfTheEnd()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var buffer = Filled(pool, 3000);
        buffer.RemoveFirst(10);

        var detached = buffer.Detach(200);

        AssertFrames(detached, 10, 2990);
        Assert.Equal(3, detached.ChunkCount);
        AssertFrames(buffer, 2800, 200);
        Assert.Equal(1, buffer.ChunkCount);

        // What's recorded next goes in other chunks, even ones given back and taken again.
        buffer.Clear();

        for (var i = 0; i < 2000; i++)
        {
            buffer.Add(Frame(-1));
        }

        AssertFrames(detached, 10, 2990);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(5000, 3000)]
    public void DetachKeepsAtMostWhatThereIs(int keep, int kept)
    {
        var buffer = Filled(new ReplayFrameChunkPool(16), 3000);

        var detached = buffer.Detach(keep);

        Assert.Equal(3000, detached.Count);
        AssertFrames(buffer, 3000 - kept, kept);
    }

    [Fact]
    public void ACopiedRangeHasChunksOfItsOwn()
    {
        var pool   = new ReplayFrameChunkPool(16);
        var buffer = Filled(pool, 3000);

        var copy = buffer.CopyRange(1000, 1500);

        AssertFrames(copy, 1000, 1500);
        Assert.Equal(2, copy.ChunkCount);

        buffer.Clear();
        Filled(pool, 3000, from: -5000);

        AssertFrames(copy, 1000, 1500);
    }

    [Fact]
    public void ARangePastTheFramesIsRefused()
    {
        var buffer = Filled(new ReplayFrameChunkPool(1), 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CopyRange(5, 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.CopyRange(-1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[10]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.RemoveFirst(11));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Truncate(11));
    }

    [Fact]
    public void ThePoolTakesBackChunksUpToItsLimit()
    {
        var pool   = new ReplayFrameChunkPool(1);
        var first  = pool.Rent();
        var second = pool.Rent();

        pool.Return([first, second]);

        Assert.Equal(1, pool.SpareCount);
        Assert.Same(first, pool.Rent());
        Assert.Equal(ReplayFrameBuffer.ChunkFrames, first.Length);
    }
}
