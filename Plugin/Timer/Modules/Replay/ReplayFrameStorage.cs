using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Sharp.Shared.Enums;
using Sharp.Shared.Types;
using Source2Surf.Timer.Shared.Models.Replay;

namespace Source2Surf.Timer.Modules.Replay;

/// <summary>
/// Versioned disk representation. The runtime ReplayFrameData layout must remain unchanged
/// because version 1 stores its raw memory, including padding.
/// </summary>
internal sealed class ReplayFrameStorage
{
    public const int LegacyVersion = 1;
    public const int CompactVersion = 2;

    private const int CompactFrameSize = 35;
    private const int Int24Min = -(1 << 23);
    private const int Int24Max = (1 << 23) - 1;
    private const float QuantizationScale = 32f;
    private const float InverseQuantizationScale = 1f / QuantizationScale;

    private const int WriteBufferSize = 64 * 1024;
    private readonly IReadOnlyList<ReplayFrameData> _frames;
    private readonly int _frameCount;

    public int Version { get; }
    public int FrameSize => Version == LegacyVersion ? Unsafe.SizeOf<ReplayFrameData>() : CompactFrameSize;
    public int SerializedSize => checked(sizeof(int) + _frameCount * FrameSize);

    // The caller owns a stable snapshot for the entire save operation. Main snapshots detach
    // their list; stage snapshots copy their range before background work starts.
    public ReplayFrameStorage(IReadOnlyList<ReplayFrameData> frames)
    {
        _frames = frames;
        _frameCount = frames.Count;
        Version = CompactVersion;
        for (var i = 0; i < _frameCount; i++)
        {
            if (!CanPackSpatialFields(frames[i]))
            {
                Version = LegacyVersion;
                break;
            }
        }
    }

    public byte[] Serialize()
    {
        var payload = new byte[SerializedSize];
        Serialize(payload);
        return payload;
    }

    public void Serialize(Span<byte> destination)
    {
        ValidateSnapshot();
        if (destination.Length != SerializedSize)
            throw new ArgumentException("Destination must match the serialized replay size.", nameof(destination));

        // Same wire contract as MemoryPack's unmanaged array formatter, without an
        // intermediate CompactFrame[] or a global formatter that could change V1 behavior.
        BinaryPrimitives.WriteInt32LittleEndian(destination, _frameCount);
        WriteFrames(destination[sizeof(int)..], 0, _frameCount);
    }

    public async ValueTask SerializeAsync(Stream stream)
    {
        ValidateSnapshot();
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(SerializedSize, WriteBufferSize));
        try
        {
            var capacity = Math.Min(buffer.Length, WriteBufferSize);
            BinaryPrimitives.WriteInt32LittleEndian(buffer, _frameCount);
            var offset = sizeof(int);
            var frameIndex = 0;
            do
            {
                var count = Math.Min((capacity - offset) / FrameSize, _frameCount - frameIndex);
                var bytes = count * FrameSize;
                WriteFrames(buffer.AsSpan(offset, bytes), frameIndex, count);
                await stream.WriteAsync(buffer.AsMemory(0, offset + bytes)).ConfigureAwait(false);
                frameIndex += count;
                offset = 0;
            } while (frameIndex < _frameCount);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void ValidateSnapshot()
    {
        if (_frames.Count != _frameCount)
            throw new InvalidOperationException("Replay snapshot changed during serialization.");
    }

    private void WriteFrames(Span<byte> destination, int start, int count)
    {
        if (Version == CompactVersion)
        {
            var compact = MemoryMarshal.Cast<byte, CompactFrame>(destination);
            for (var i = 0; i < count; i++)
                compact[i] = new CompactFrame(_frames[start + i]);
        }
        else
        {
            // Clear pooled memory as V1 exposes runtime struct padding on disk.
            destination.Clear();
            var legacy = MemoryMarshal.Cast<byte, ReplayFrameData>(destination);
            for (var i = 0; i < count; i++)
                legacy[i] = Quantize(_frames[start + i]);
        }
    }

    public static ReplayFrameData[]? Deserialize(ReadOnlySpan<byte> payload, int version)
    {
        var frameSize = version switch
        {
            LegacyVersion => Unsafe.SizeOf<ReplayFrameData>(),
            CompactVersion => CompactFrameSize,
            _ => throw new InvalidDataException($"Unsupported replay version: {version}."),
        };
        // MemoryPack unmanaged arrays have a 4-byte count followed by raw frame bytes.
        // Reject truncated/mislabeled data before allocating, and do not silently ignore trailing bytes.
        if (payload.Length < sizeof(int))
            throw new InvalidDataException("Missing replay frame count.");

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < -1 || payload.Length != sizeof(int) + (long)Math.Max(count, 0) * frameSize)
            throw new InvalidDataException("Replay frame payload length does not match its format.");

        if (count == -1)
            return null;
        if (count == 0)
            return [];

        // The span is a borrowed view of the payload, not a materialized frame array.
        // Only the final runtime array escapes, so callers can immediately return pooled bytes.
        var frames = new ReplayFrameData[count];
        var frameBytes = payload[sizeof(int)..];
        if (version == LegacyVersion)
        {
            frameBytes.CopyTo(MemoryMarshal.AsBytes(frames.AsSpan()));
        }
        else
        {
            var packed = MemoryMarshal.Cast<byte, CompactFrame>(frameBytes);
            for (var i = 0; i < frames.Length; i++)
                frames[i] = packed[i].ToRuntime();
        }

        return frames;
    }

    // Preserve the existing 1/32-unit position, velocity and angle quantization.
    private static ReplayFrameData Quantize(ReplayFrameData frame)
        => frame with
        {
            Origin = new Vector(Snap(frame.Origin.X), Snap(frame.Origin.Y), Snap(frame.Origin.Z)),
            Angles = new Vector2D(Snap(frame.Angles.X), Snap(frame.Angles.Y)),
            Velocity = new Vector(Snap(frame.Velocity.X), Snap(frame.Velocity.Y), Snap(frame.Velocity.Z)),
        };

    private static bool CanPackSpatialFields(ReplayFrameData frame)
    {
        return FitsFixed(frame.Origin.X, Int24Min, Int24Max)
               && FitsFixed(frame.Origin.Y, Int24Min, Int24Max)
               && FitsFixed(frame.Origin.Z, Int24Min, Int24Max)
               && FitsFixed(frame.Velocity.X, Int24Min, Int24Max)
               && FitsFixed(frame.Velocity.Y, Int24Min, Int24Max)
               && FitsFixed(frame.Velocity.Z, Int24Min, Int24Max)
               && FitsFixed(frame.Angles.X, short.MinValue, short.MaxValue)
               && FitsFixed(frame.Angles.Y, short.MinValue, short.MaxValue);
    }

    private static bool FitsFixed(float value, int minimum, int maximum)
    {
        var quantized = QuantizeToInteger(value);
        // Comparisons also reject NaN and infinities; check the rounded value, not the input.
        return quantized >= minimum && quantized <= maximum;
    }

    private static int PackFixed(float value, int minimum, int maximum)
    {
        var quantized = QuantizeToInteger(value);
        if (!(quantized >= minimum && quantized <= maximum))
            throw new InvalidDataException("Spatial value requires the full-width replay format.");

        return checked((int)quantized);
    }

    // A double intermediate keeps finite float extremes finite on the V1 fallback path.
    // Multiplication by 32 is exact, and ties still round to even as in the original MathF.Round.
    private static double QuantizeToInteger(float value) => Math.Round((double)value * QuantizationScale);

    private static float Snap(float value)
        => float.IsFinite(value)
            ? (float)(QuantizeToInteger(value) * InverseQuantizationScale)
            : value;

    // Current V2 writes use 35 bytes: position, velocity, angles, button masks, MoveType.
    // Pack=1 avoids padding; the disk layout does not depend on ModSharp vector types.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct CompactFrame
    {
        private readonly SignedInt24 _originX;
        private readonly SignedInt24 _originY;
        private readonly SignedInt24 _originZ;
        private readonly SignedInt24 _velocityX;
        private readonly SignedInt24 _velocityY;
        private readonly SignedInt24 _velocityZ;
        private readonly short _angleX;
        private readonly short _angleY;
        private readonly uint _pressedButtons;
        private readonly uint _changedButtons;
        private readonly uint _scrollButtons;
        private readonly byte _moveType;

        public CompactFrame(ReplayFrameData frame)
        {
            _originX = new SignedInt24(PackFixed(frame.Origin.X, Int24Min, Int24Max));
            _originY = new SignedInt24(PackFixed(frame.Origin.Y, Int24Min, Int24Max));
            _originZ = new SignedInt24(PackFixed(frame.Origin.Z, Int24Min, Int24Max));
            _velocityX = new SignedInt24(PackFixed(frame.Velocity.X, Int24Min, Int24Max));
            _velocityY = new SignedInt24(PackFixed(frame.Velocity.Y, Int24Min, Int24Max));
            _velocityZ = new SignedInt24(PackFixed(frame.Velocity.Z, Int24Min, Int24Max));
            _angleX = checked((short)PackFixed(frame.Angles.X, short.MinValue, short.MaxValue));
            _angleY = checked((short)PackFixed(frame.Angles.Y, short.MinValue, short.MaxValue));
            // V2 stores only engine bits 0..31. Truncation is intentional, even in checked builds.
            _pressedButtons = unchecked((uint)(ulong)frame.PressedButtons);
            _changedButtons = unchecked((uint)(ulong)frame.ChangedButtons);
            _scrollButtons = unchecked((uint)(ulong)frame.ScrollButtons);
            _moveType = (byte)frame.MoveType;
        }

        public ReplayFrameData ToRuntime()
            => new()
            {
                Origin = new Vector(_originX.Value * InverseQuantizationScale,
                                    _originY.Value * InverseQuantizationScale,
                                    _originZ.Value * InverseQuantizationScale),
                Velocity = new Vector(_velocityX.Value * InverseQuantizationScale,
                                      _velocityY.Value * InverseQuantizationScale,
                                      _velocityZ.Value * InverseQuantizationScale),
                Angles = new Vector2D(_angleX * InverseQuantizationScale, _angleY * InverseQuantizationScale),
                PressedButtons = (UserCommandButtons)_pressedButtons,
                ChangedButtons = (UserCommandButtons)_changedButtons,
                ScrollButtons = (UserCommandButtons)_scrollButtons,
                MoveType = (MoveType)_moveType,
            };
    }

    // Little-endian two's-complement int24. A signed high byte performs sign extension on decode.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct SignedInt24
    {
        private readonly byte _low;
        private readonly byte _middle;
        private readonly sbyte _high;

        public SignedInt24(int value)
        {
            _low = (byte)(value & 0xFF);
            _middle = (byte)((value >> 8) & 0xFF);
            _high = checked((sbyte)(value >> 16));
        }

        public int Value => _low | (_middle << 8) | (_high << 16);
    }
}
