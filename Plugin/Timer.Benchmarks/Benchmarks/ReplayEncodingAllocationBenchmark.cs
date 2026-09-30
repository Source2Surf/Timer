using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Microsoft.Extensions.Logging.Abstractions;
using Sharp.Shared.Types;
using Source2Surf.Timer.Benchmarks.Workload;
using Source2Surf.Timer.Modules.Replay;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Benchmarks.Benchmarks;

public enum ReplayEncodingLayout
{
    Compact,
    LateLegacy,
}

[MemoryDiagnoser]
[MinIterationTime(250)]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ReplayEncodingAllocationBenchmark
{
    private const float QuantizationScale = 32f;
    private const float InverseQuantizationScale = 1f / QuantizationScale;

    private ReplayFrameData[] _frames = null!;
    private ReplayFrameStorage _preparedStorage = null!;
    private byte[] _destination = null!;

    [Params(19200, 38400, 115200)]
    public int FrameCount { get; set; }

    [Params(ReplayEncodingLayout.Compact, ReplayEncodingLayout.LateLegacy)]
    public ReplayEncodingLayout Layout { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _frames = ReplayWorkload.CreateFrames(FrameCount);
        if (Layout == ReplayEncodingLayout.LateLegacy)
        {
            var last = _frames[^1];
            _frames[^1] = last with
            {
                Origin = new Vector(300000f, last.Origin.Y, last.Origin.Z),
            };
        }

        _preparedStorage = new ReplayFrameStorage(_frames);
        _destination = new byte[_preparedStorage.SerializedSize];
        ValidateLayoutAndRoundtrip();
        Console.WriteLine(
            $"REPLAY_ENCODING_SETUP frameCount={FrameCount} layout={Layout} "
          + $"version={_preparedStorage.Version} serializedBytes={_preparedStorage.SerializedSize}");
    }

    [Benchmark]
    public int SelectLayout()
    {
        var storage = new ReplayFrameStorage(_frames);
        var version = storage.Version;
        GC.KeepAlive(storage);
        return version;
    }

    [Benchmark]
    public int EncodePrepared()
    {
        _preparedStorage.Serialize(_destination);
        GC.KeepAlive(_destination);
        return _destination.Length;
    }

    [Benchmark]
    public int SelectAndEncode()
    {
        var storage = new ReplayFrameStorage(_frames);
        storage.Serialize(_destination);
        GC.KeepAlive(storage);
        GC.KeepAlive(_destination);
        return storage.Version ^ _destination.Length;
    }

    private void ValidateLayoutAndRoundtrip()
    {
        var expectedVersion = Layout == ReplayEncodingLayout.Compact
            ? ReplayFrameStorage.CompactVersion
            : ReplayFrameStorage.LegacyVersion;
        Require(_preparedStorage.Version == expectedVersion,
                $"{Layout} selected replay version {_preparedStorage.Version}, expected {expectedVersion}.");

        var expectedFrameSize = expectedVersion == ReplayFrameStorage.CompactVersion
            ? 35
            : Unsafe.SizeOf<ReplayFrameData>();
        Require(_preparedStorage.SerializedSize == sizeof(int) + FrameCount * expectedFrameSize,
                "Prepared storage size does not match the selected wire layout.");

        if (Layout == ReplayEncodingLayout.LateLegacy)
        {
            var prefix = new ArraySegment<ReplayFrameData>(_frames, 0, _frames.Length - 1);
            Require(new ReplayFrameStorage(prefix).Version == ReplayFrameStorage.CompactVersion,
                    "LateLegacy input stopped qualifying for compact storage before its final frame.");
        }

        _preparedStorage.Serialize(_destination);
        var directFrames = ReplayFrameStorage.Deserialize(_destination, _preparedStorage.Version)
                           ?? throw new InvalidOperationException("Direct encoding produced a null frame array.");
        VerifyFrames(directFrames);

        var bytes = ReplayShared.SerializeReplay(new ReplayFileHeader
        {
            Version = 99,
            SteamId = 76561198000000001,
            TotalFrames = FrameCount,
            PreFrame = 0,
            PostFrame = FrameCount,
            Time = FrameCount / 64f,
            PlayerName = "allocation-audit",
        }, _frames);
        var loaded = ReplayShared.DeserializeReplay(bytes, 0, 0, 0, NullLogger.Instance)
                     ?? throw new InvalidOperationException("Complete encoded replay failed to deserialize.");
        Require(loaded.Content.Header.Version == expectedVersion,
                "Complete replay header does not contain the selected storage version.");
        VerifyFrames(loaded.Content.Frames);
    }

    private void VerifyFrames(IReadOnlyList<ReplayFrameData> actual)
    {
        Require(actual.Count == _frames.Length,
                "Encoded replay frame count changed during roundtrip.");
        for (var i = 0; i < _frames.Length; i++)
        {
            var expected = QuantizeSpatial(_frames[i]);
            Require(actual[i].Equals(expected),
                    $"Encoded replay frame {i} changed outside the storage quantization contract.");
        }
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
