using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class QueryBenchmark
{
    private ClosestFrameIndex _index = null!;
    private ReplayQuery[]     _queries = null!;
    private int               _cursor;

    [Params(5000, 50000, 230000)]
    public int FrameCount { get; set; }

    [Params(QueryScenario.OnPath, QueryScenario.Random)]
    public QueryScenario Scenario { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var frames = ReplayWorkload.CreateFrames(FrameCount);
        _index   = new ClosestFrameIndex(frames);
        _queries = ReplayWorkload.CreateQueries(frames, Scenario);
        _cursor  = 0;
    }

    [Benchmark]
    public int FindClosest()
    {
        var query = _queries[_cursor++ & (ReplayWorkload.QueryCount - 1)];
        var index = _index.FindClosest(query.Position, query.PreferredFrame, out var distanceSquared);
        return index ^ BitConverter.SingleToInt32Bits(distanceSquared);
    }
}
