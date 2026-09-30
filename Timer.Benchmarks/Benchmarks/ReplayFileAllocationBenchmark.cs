using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Types;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared;
using Source2Surf.Timer.Shared.Models.Replay;
using ZstdSharp;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReplayFileAllocationBenchmark
{
    private const int CompressionWorkers = 0;
    private const float QuantizationScale = 32f;
    private const float InverseQuantizationScale = 1f / QuantizationScale;

    private ReplayFrameData[] _frames = null!;
    private ReplayFileHeader _header = null!;
    private byte[] _fixtureBytes = null!;
    private Decompressor _decompressor = null!;
    private string _tempDirectory = null!;
    private string _fixturePath = null!;
    private string _savePath = null!;
    private string _emptyPath = null!;

    [Params(19200, 38400, 115200)]
    public int FrameCount { get; set; }

    [Params(0, 3)]
    public int CompressionLevel { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"timer-replay-allocation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _fixturePath = Path.Combine(_tempDirectory, "fixture.replay");
        _savePath = Path.Combine(_tempDirectory, "benchmark-save.replay");
        _emptyPath = Path.Combine(_tempDirectory, "empty.replay");

        _frames = ReplayWorkload.CreateFrames(FrameCount);
        _header = CreateHeader(FrameCount);
        _decompressor = new Decompressor();

        await WriteRequiredAsync(_header, _fixturePath, _frames).ConfigureAwait(false);
        _fixtureBytes = await File.ReadAllBytesAsync(_fixturePath).ConfigureAwait(false);
        var secondRead = await File.ReadAllBytesAsync(_fixturePath).ConfigureAwait(false);
        Require(_fixtureBytes.AsSpan().SequenceEqual(secondRead),
                "Fixture bytes differ between complete reads.");

        ValidateLoadedReplay(_fixtureBytes, _fixturePath, _frames, _header);
        await ValidateOwnedBytesAfterLaterWriteAsync().ConfigureAwait(false);
        await ValidateEmptyReplayAsync().ConfigureAwait(false);

        var saveOnlyMedian = await MeasureProcessAllocationsAsync(SaveOnly).ConfigureAwait(false);
        var saveThenReadMedian = await MeasureProcessAllocationsAsync(SaveThenReadForUpload).ConfigureAwait(false);
        var readSavedMedian = await MeasureProcessAllocationsAsync(ReadSavedFile).ConfigureAwait(false);
        WriteSetupDiagnostics(saveOnlyMedian, saveThenReadMedian, readSavedMedian);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _decompressor?.Dispose();
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    [Benchmark]
    public async Task SaveOnly()
        => await WriteRequiredAsync(_header, _savePath, _frames).ConfigureAwait(false);

    [Benchmark]
    public async Task<byte[]> SaveThenReadForUpload()
    {
        await WriteRequiredAsync(_header, _savePath, _frames).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(_savePath).ConfigureAwait(false);
    }

    [Benchmark]
    public Task<byte[]> ReadSavedFile() => File.ReadAllBytesAsync(_fixturePath);

    [Benchmark]
    public int LoadOwnedBytes()
    {
        var loaded = ReplayShared.DeserializeReplay(_fixtureBytes, 0, 0, 0, NullLogger.Instance)
                     ?? throw new InvalidOperationException("Owned fixture bytes failed to load.");
        var count = loaded.Content.Frames.Count;
        GC.KeepAlive(loaded.Content.Frames);
        return count;
    }

    [Benchmark]
    public int LoadSavedFile()
    {
        var loaded = ReplayShared.LoadReplayFromPath(_fixturePath, 0, 0, 0, _decompressor, NullLogger.Instance)
                     ?? throw new InvalidOperationException("Saved fixture file failed to load.");
        var count = loaded.Content.Frames.Count;
        GC.KeepAlive(loaded.Content.Frames);
        return count;
    }

    private async Task ValidateOwnedBytesAfterLaterWriteAsync()
    {
        var ownedUploadBytes = await SaveThenReadForUpload().ConfigureAwait(false);
        var originalBytes = ownedUploadBytes.ToArray();
        var replacement = CreateReplacementFrames();
        var originalStorage = new ReplayFrameStorage(_frames);
        var replacementStorage = new ReplayFrameStorage(replacement);
        Require(originalStorage.Version == replacementStorage.Version
                && originalStorage.SerializedSize == replacementStorage.SerializedSize,
                "Replacement fixture does not have the same logical storage size.");

        await WriteRequiredAsync(_header, _savePath, replacement).ConfigureAwait(false);
        Require(ownedUploadBytes.AsSpan().SequenceEqual(originalBytes),
                "Owned upload bytes changed after a later same-size write.");

        var loaded = ReplayShared.DeserializeReplay(ownedUploadBytes, 0, 0, 0, NullLogger.Instance)
                     ?? throw new InvalidOperationException("Owned upload bytes failed to load after a later write.");
        VerifyLoadedResult(loaded, _frames, _header);
    }

    private async Task ValidateEmptyReplayAsync()
    {
        var header = CreateHeader(0);
        await WriteRequiredAsync(header, _emptyPath, Array.Empty<ReplayFrameData>()).ConfigureAwait(false);
        var bytes = await File.ReadAllBytesAsync(_emptyPath).ConfigureAwait(false);
        var memoryLoaded = ReplayShared.DeserializeReplay(bytes, 0, 0, 0, NullLogger.Instance)
                           ?? throw new InvalidOperationException("Empty replay bytes failed to load.");
        var fileLoaded = ReplayShared.LoadReplayFromPath(_emptyPath, 0, 0, 0, _decompressor, NullLogger.Instance)
                         ?? throw new InvalidOperationException("Empty replay file failed to load.");
        Require(memoryLoaded.Content.Header.Version == ReplayFrameStorage.CompactVersion
                && memoryLoaded.Content.Frames.Count == 0
                && fileLoaded.Content.Header.Version == ReplayFrameStorage.CompactVersion
                && fileLoaded.Content.Frames.Count == 0,
                "Empty replay roundtrip changed its version or frame count.");
    }

    private void ValidateLoadedReplay(byte[] bytes, string path, ReplayFrameData[] expectedFrames,
                                      ReplayFileHeader expectedHeader)
    {
        var memoryLoaded = ReplayShared.DeserializeReplay(bytes, 0, 0, 0, NullLogger.Instance)
                           ?? throw new InvalidOperationException("Fixture bytes failed to load.");
        var fileLoaded = ReplayShared.LoadReplayFromPath(path, 0, 0, 0, _decompressor, NullLogger.Instance)
                         ?? throw new InvalidOperationException("Fixture file failed to load.");
        VerifyLoadedResult(memoryLoaded, expectedFrames, expectedHeader);
        VerifyLoadedResult(fileLoaded, expectedFrames, expectedHeader);
    }

    private static void VerifyLoadedResult(ReplayLoadResult loaded, ReplayFrameData[] expectedFrames,
                                           ReplayFileHeader expectedHeader)
    {
        Require(loaded.Content.Header.Version == ReplayFrameStorage.CompactVersion,
                "Fixture selected an unexpected storage version.");
        Require(loaded.Content.Header.TotalFrames == expectedHeader.TotalFrames
                && loaded.Content.Header.PreFrame == expectedHeader.PreFrame
                && loaded.Content.Header.PostFrame == expectedHeader.PostFrame
                && loaded.Content.Header.Time.Equals(expectedHeader.Time)
                && loaded.Content.Header.PlayerName == expectedHeader.PlayerName
                && loaded.Content.Header.StageTicks is not null
                && expectedHeader.StageTicks is not null
                && loaded.Content.Header.StageTicks.SequenceEqual(expectedHeader.StageTicks),
                "Fixture header changed during roundtrip.");
        Require(loaded.Content.Frames.Count == expectedFrames.Length,
                "Fixture frame count changed during roundtrip.");

        for (var i = 0; i < expectedFrames.Length; i++)
        {
            var expected = QuantizeSpatial(expectedFrames[i]);
            Require(loaded.Content.Frames[i].Equals(expected),
                    $"Fixture frame {i} changed outside the storage quantization contract.");
        }
    }

    private ReplayFrameData[] CreateReplacementFrames()
    {
        var replacement = new ReplayFrameData[_frames.Length];
        for (var i = 0; i < replacement.Length; i++)
        {
            var frame = _frames[i];
            replacement[i] = frame with
            {
                Origin = new Vector(frame.Origin.X + 64f, frame.Origin.Y, frame.Origin.Z),
            };
        }

        return replacement;
    }

    private async Task WriteRequiredAsync(ReplayFileHeader header, string path,
                                          IReadOnlyList<ReplayFrameData> frames)
    {
        if (!await ReplayShared.WriteReplayToFileAsync(header,
                                                       path,
                                                       frames,
                                                       CompressionLevel,
                                                       CompressionWorkers,
                                                       NullLogger.Instance)
                               .ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Replay write failed for {path}.");
        }
    }

    private void WriteSetupDiagnostics(long saveOnlyMedian, long saveThenReadMedian, long readSavedMedian)
    {
        var fileBytes = _fixtureBytes.LongLength;
        const string scope = "process-wide allocated-byte delta median; includes runtime background noise; not peak RSS";
        Console.WriteLine(
            $"REPLAY_FILE_SETUP frameCount={FrameCount} compressionLevel={CompressionLevel} "
          + $"compressionWorkers={CompressionWorkers} fileBytes={fileBytes}");
        Console.WriteLine(
            $"REPLAY_FILE_PROCESS_ALLOC frameCount={FrameCount} compressionLevel={CompressionLevel} "
          + $"saveOnlyMedianBytes={saveOnlyMedian} saveThenReadMedianBytes={saveThenReadMedian} "
          + $"readSavedMedianBytes={readSavedMedian} scope=process-wide-background-noise-not-rss");

        var artifactDirectory = Environment.GetEnvironmentVariable("REPLAY_AUDIT_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifactDirectory))
            return;

        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(
            artifactDirectory,
            $"ReplayFileAllocationBenchmark-{FrameCount}-level-{CompressionLevel}-pid-{Environment.ProcessId}-{Guid.NewGuid():N}.json");
        var record = new
        {
            FrameCount,
            CompressionLevel,
            CompressionWorkers,
            FileBytes = fileBytes,
            SaveOnlyMedianProcessAllocatedBytes = saveOnlyMedian,
            SaveThenReadMedianProcessAllocatedBytes = saveThenReadMedian,
            ReadSavedFileMedianProcessAllocatedBytes = readSavedMedian,
            WarmupCount = 3,
            SampleCount = 11,
            Scope = scope,
        };
        File.WriteAllText(artifactPath, JsonSerializer.Serialize(record, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }

    private static async Task<long> MeasureProcessAllocationsAsync(Func<Task> operation)
    {
        for (var i = 0; i < 3; i++)
            await operation().ConfigureAwait(false);

        var samples = new long[11];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            await operation().ConfigureAwait(false);
            samples[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static async Task<long> MeasureProcessAllocationsAsync<T>(Func<Task<T>> operation)
    {
        for (var i = 0; i < 3; i++)
            GC.KeepAlive(await operation().ConfigureAwait(false));

        var samples = new long[11];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var result = await operation().ConfigureAwait(false);
            samples[i] = GC.GetTotalAllocatedBytes(precise: true) - before;
            GC.KeepAlive(result);
        }

        Array.Sort(samples);
        return samples[samples.Length / 2];
    }

    private static ReplayFrameData QuantizeSpatial(ReplayFrameData frame)
        => frame with
        {
            Origin = new Vector(Snap(frame.Origin.X), Snap(frame.Origin.Y), Snap(frame.Origin.Z)),
            Angles = new Vector2D(Snap(frame.Angles.X), Snap(frame.Angles.Y)),
            Velocity = new Vector(Snap(frame.Velocity.X), Snap(frame.Velocity.Y), Snap(frame.Velocity.Z)),
        };

    private static float Snap(float value)
        => float.IsFinite(value)
            ? (float) (Math.Round((double) value * QuantizationScale) * InverseQuantizationScale)
            : value;

    private static ReplayFileHeader CreateHeader(int frameCount)
        => new()
        {
            Version = 99,
            SteamId = 76561198000000001,
            TotalFrames = frameCount,
            PreFrame = 0,
            PostFrame = frameCount,
            Time = frameCount * TimerConstants.TickInterval,
            PlayerName = "allocation-audit",
            StageTicks = frameCount == 0 ? [] : [0, frameCount],
        };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
