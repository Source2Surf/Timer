using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class BuildBenchmark
{
    private ReplayFrameData[] _frames = null!;

    [Params(5000, 50000, 230000)]
    public int FrameCount { get; set; }

    [GlobalSetup]
    public void Setup() => _frames = ReplayWorkload.CreateFrames(FrameCount);

    [Benchmark]
    public object Build() => new ClosestFrameIndex(_frames);
}
