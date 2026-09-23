using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReplaySnapshotAllocationBenchmark
{
    private const int InitialCapacity = TimerConstants.Tickrate * 60 * 5;
    private const string PlayerName = "allocation-audit";

    private ReplayFrameData[] _frames = null!;
    private PlayerFrameData _mainFrame = null!;
    private PlayerFrameData _stageFrame = null!;

    [Params(19200, 38400, 115200)]
    public int FrameCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frames = ReplayWorkload.CreateFrames(FrameCount);
        ValidateSnapshotOwnership();
        WriteCapacityDiagnostics();
        _mainFrame = CreateRecordedFrame(_frames);
        _stageFrame = CreateRecordedFrame(_frames);
    }

    [IterationSetup(Target = nameof(MainSnapshot))]
    public void PrepareMainSnapshot() => _mainFrame = CreateRecordedFrame(_frames);

    [Benchmark]
    [InvocationCount(1)]
    public int MainSnapshot()
    {
        var snapshot = ReplayShared.CreateMainReplaySnapshot(_mainFrame);
        var count = snapshot.Header.TotalFrames;
        GC.KeepAlive(snapshot.Frames);
        GC.KeepAlive(_mainFrame.Frames);
        return count;
    }

    [Benchmark]
    public int StageSnapshot()
    {
        var snapshot = ReplayShared.CreateStageReplaySnapshot(_stageFrame,
                                                              startTick: 0,
                                                              stageStartFrame: 0,
                                                              stageFinishFrame: FrameCount,
                                                              postRunFrameCount: 0,
                                                              finishTime: FrameCount * TimerConstants.TickInterval);
        var count = snapshot.Header.TotalFrames;
        GC.KeepAlive(snapshot.Frames);
        return count;
    }

    [Benchmark]
    public int RecordAndFreeze()
    {
        var frame = CreateEmptyFrame(FrameCount);
        for (var i = 0; i < _frames.Length; i++)
            frame.Frames.Add(_frames[i]);

        var snapshot = ReplayShared.CreateMainReplaySnapshot(frame);
        var count = snapshot.Header.TotalFrames;
        GC.KeepAlive(snapshot.Frames);
        GC.KeepAlive(frame.Frames);
        return count;
    }

    private void ValidateSnapshotOwnership()
    {
        var expectedFirst = _frames[0];
        var expectedLast = _frames[^1];

        var mainProbe = CreateRecordedFrame(_frames);
        mainProbe.NewStageTicks.Add(FrameCount / 2);
        var mainSnapshot = ReplayShared.CreateMainReplaySnapshot(mainProbe);
        Require(mainSnapshot.Header.TotalFrames == FrameCount, "Main snapshot frame count changed.");
        Require(mainSnapshot.Header.PreFrame == 0 && mainSnapshot.Header.PostFrame == FrameCount,
                "Main snapshot markers changed.");
        Require(mainSnapshot.Header.StageTicks is { Count: 1 }
                && mainSnapshot.Header.StageTicks[0] == FrameCount / 2,
                "Main snapshot stage markers changed.");
        Require(mainSnapshot.Frames.Count == FrameCount
                && mainSnapshot.Frames[0].Equals(expectedFirst)
                && mainSnapshot.Frames[^1].Equals(expectedLast),
                "Main snapshot contents changed before source mutation.");
        mainProbe.Frames.Add(default);
        mainProbe.Frames.Clear();
        Require(mainSnapshot.Frames.Count == FrameCount
                && mainSnapshot.Frames[0].Equals(expectedFirst)
                && mainSnapshot.Frames[^1].Equals(expectedLast),
                "Main snapshot aliases the replacement live buffer.");

        var stageProbe = CreateRecordedFrame(_frames);
        var stageSnapshot = ReplayShared.CreateStageReplaySnapshot(stageProbe,
                                                                   startTick: 0,
                                                                   stageStartFrame: 0,
                                                                   stageFinishFrame: FrameCount,
                                                                   postRunFrameCount: 0,
                                                                   finishTime: FrameCount * TimerConstants.TickInterval);
        Require(stageSnapshot.Header.TotalFrames == FrameCount,
                "Stage snapshot frame count changed.");
        Require(stageSnapshot.Header.PreFrame == 0 && stageSnapshot.Header.PostFrame == FrameCount,
                "Stage snapshot markers changed.");
        Require(stageSnapshot.Frames.Count == FrameCount
                && stageSnapshot.Frames[0].Equals(expectedFirst)
                && stageSnapshot.Frames[^1].Equals(expectedLast),
                "Stage snapshot contents changed before source mutation.");
        stageProbe.Frames[0] = default;
        stageProbe.Frames.Clear();
        Require(stageSnapshot.Frames.Count == FrameCount
                && stageSnapshot.Frames[0].Equals(expectedFirst)
                && stageSnapshot.Frames[^1].Equals(expectedLast),
                "Stage snapshot aliases the live recording list.");

        GC.KeepAlive(mainSnapshot.Frames);
        GC.KeepAlive(stageSnapshot.Frames);
    }

    private void WriteCapacityDiagnostics()
    {
        Console.WriteLine(
            "REPLAY_SNAPSHOT_SCOPE units=per-operation backingBytes=structural-not-rss "
          + "mainTiming=single-invocation-may-be-noisy");
        var current = CreateRecordedFrame(_frames);
        var currentSnapshot = ReplayShared.CreateMainReplaySnapshot(current);
        WriteCapacityLine("current", 1, FrameCount,
                          ((List<ReplayFrameData>) currentSnapshot.Frames).Capacity,
                          current.Frames.Capacity);

        TraceCapacitySequence($"equal-{FrameCount}", FrameCount, FrameCount, FrameCount);
        TraceCapacitySequence("long-then-short", 115200, 19200);
        GC.KeepAlive(currentSnapshot.Frames);
    }

    private static void TraceCapacitySequence(string scenario, params int[] frameCounts)
    {
        var frame = CreateEmptyFrame(frameCounts[0]);
        IReadOnlyList<ReplayFrameData>? latestFrames = null;

        for (var step = 0; step < frameCounts.Length; step++)
        {
            var count = frameCounts[step];
            frame.TimerFinishFrame = count;
            frame.FinishTime = count * TimerConstants.TickInterval;
            for (var i = 0; i < count; i++)
                frame.Frames.Add(default);

            var snapshot = ReplayShared.CreateMainReplaySnapshot(frame);
            latestFrames = snapshot.Frames;
            WriteCapacityLine(scenario, step + 1, count,
                              ((List<ReplayFrameData>) snapshot.Frames).Capacity,
                              frame.Frames.Capacity);
        }

        GC.KeepAlive(latestFrames);
        GC.KeepAlive(frame.Frames);
    }

    private static void WriteCapacityLine(string scenario, int step, int count, int completedCapacity,
                                          int liveCapacity)
    {
        var backingBytes = checked((long) (completedCapacity + liveCapacity)
                                  * Unsafe.SizeOf<ReplayFrameData>());
        Console.WriteLine(
            $"REPLAY_SNAPSHOT_CAPACITY scenario={scenario} step={step} count={count} "
          + $"completedCapacity={completedCapacity} liveCapacity={liveCapacity} backingBytes={backingBytes}");
    }

    private static PlayerFrameData CreateRecordedFrame(ReplayFrameData[] frames)
    {
        var frame = CreateEmptyFrame(frames.Length);
        for (var i = 0; i < frames.Length; i++)
            frame.Frames.Add(frames[i]);
        return frame;
    }

    private static PlayerFrameData CreateEmptyFrame(int finishFrame)
        => new()
        {
            Frames = new List<ReplayFrameData>(InitialCapacity),
            Name = PlayerName,
            TimerStartFrame = 0,
            TimerFinishFrame = finishFrame,
            FinishTime = finishFrame * TimerConstants.TickInterval,
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
