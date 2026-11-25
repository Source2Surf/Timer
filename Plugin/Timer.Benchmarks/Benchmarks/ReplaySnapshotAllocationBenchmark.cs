using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

// Each snapshot is released once read, as a save that playback doesn't keep releases it, so its chunks go back to the
// pool and what's measured is the steady state.
[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReplaySnapshotAllocationBenchmark
{
    private const string PlayerName = "allocation-audit";
    private const long   ChunkBytes = ReplayFrameBuffer.ChunkFrames * 64L;

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
        WriteChunkDiagnostics();
        _mainFrame = CreateRecordedFrame(_frames);
        _stageFrame = CreateRecordedFrame(_frames);
    }

    [IterationSetup(Target = nameof(MainSnapshot))]
    public void PrepareMainSnapshot() => _mainFrame = CreateRecordedFrame(_frames);

    [Benchmark]
    [InvocationCount(1)]
    public int MainSnapshot()
    {
        var snapshot = ReplayShared.CreateMainReplaySnapshot(_mainFrame, 0);
        var count = snapshot.Header.TotalFrames;
        snapshot.ReleaseFrames();
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
        snapshot.ReleaseFrames();
        return count;
    }

    [Benchmark]
    public int RecordAndFreeze()
    {
        var frame = CreateEmptyFrame(FrameCount);
        for (var i = 0; i < _frames.Length; i++)
            frame.Frames.Add(_frames[i]);

        var snapshot = ReplayShared.CreateMainReplaySnapshot(frame, 0);
        var count = snapshot.Header.TotalFrames;
        snapshot.ReleaseFrames();
        return count;
    }

    private void ValidateSnapshotOwnership()
    {
        var expectedFirst = _frames[0];
        var expectedLast = _frames[^1];

        var mainProbe = CreateRecordedFrame(_frames);
        mainProbe.NewStageTicks.Add(FrameCount / 2);
        var mainSnapshot = ReplayShared.CreateMainReplaySnapshot(mainProbe, 0);
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
        RecordOver(mainProbe);
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
        RecordOver(stageProbe);
        Require(stageSnapshot.Frames.Count == FrameCount
                && stageSnapshot.Frames[0].Equals(expectedFirst)
                && stageSnapshot.Frames[^1].Equals(expectedLast),
                "Stage snapshot aliases the live recording.");

        mainSnapshot.ReleaseFrames();
        stageSnapshot.ReleaseFrames();
    }

    // Gives the probe's chunks back and records over whatever the pool hands out again.
    private void RecordOver(PlayerFrameData probe)
    {
        probe.Frames.Clear();
        for (var i = 0; i < FrameCount; i++)
            probe.Frames.Add(default);
        probe.Frames.Clear();
    }

    private void WriteChunkDiagnostics()
    {
        Console.WriteLine(
            "REPLAY_SNAPSHOT_SCOPE units=per-operation backingBytes=structural-not-rss "
          + "mainTiming=single-invocation-may-be-noisy");
        var current = CreateRecordedFrame(_frames);
        var currentSnapshot = ReplayShared.CreateMainReplaySnapshot(current, 0);
        WriteChunkLine("current", 1, FrameCount, ((ReplayFrameBuffer) currentSnapshot.Frames).ChunkCount,
                       current.Frames.ChunkCount);
        currentSnapshot.ReleaseFrames();

        TraceChunkSequence($"equal-{FrameCount}", FrameCount, FrameCount, FrameCount);
        TraceChunkSequence("long-then-short", 115200, 19200);
    }

    private static void TraceChunkSequence(string scenario, params int[] frameCounts)
    {
        var frame = CreateEmptyFrame(frameCounts[0]);

        for (var step = 0; step < frameCounts.Length; step++)
        {
            var count = frameCounts[step];
            frame.TimerFinishFrame = count;
            frame.FinishTime = count * TimerConstants.TickInterval;
            for (var i = 0; i < count; i++)
                frame.Frames.Add(default);

            var snapshot = ReplayShared.CreateMainReplaySnapshot(frame, 0);
            WriteChunkLine(scenario, step + 1, count, ((ReplayFrameBuffer) snapshot.Frames).ChunkCount,
                           frame.Frames.ChunkCount);
            snapshot.ReleaseFrames();
        }

        frame.Frames.Clear();
    }

    private static void WriteChunkLine(string scenario, int step, int count, int completedChunks, int liveChunks)
    {
        var backingBytes = checked((completedChunks + liveChunks) * ChunkBytes);
        Console.WriteLine(
            $"REPLAY_SNAPSHOT_CHUNKS scenario={scenario} step={step} count={count} "
          + $"completedChunks={completedChunks} liveChunks={liveChunks} backingBytes={backingBytes}");
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
