# Replay storage format

A replay contains a UTF-8 JSON `ReplayFileHeader`, one newline byte (`0A`), then a
MemoryPack frame array, optionally compressed with Zstd. Both upload and local
file writers choose the layout from the frame data and set the header's `Version`.
Input headers and live frame data are not modified.

## Versions and compatibility

| Version | Layout |
| --- | --- |
| 1 | Original runtime struct: 64 bytes |
| 2 | Fixed-point spatial fields and low-32-bit buttons: 35 bytes |

A missing version defaults to V1. Each version selects exactly one frame layout;
there is no `FrameSize` header discriminator. Readers validate the exact payload
length against that layout and the array count, and reject unknown versions.
V2 has not shipped, so experimental V2 layouts have no compatibility decoder.

New writers use 35-byte V2 when every frame's rounded spatial values fit. A single
out-of-range spatial component, NaN, or infinity switches the **entire file to V1**.
Spatial values are never clamped or wrapped into a smaller integer. V2 intentionally
keeps only the low 32 bits of each button mask; high button bits do not trigger
fallback. V1 retains the existing spatial quantization and full 64-bit button
masks. Finite float extremes stay finite during fallback, and non-finite values
retain their original bits.

Both versions can be read compressed or uncompressed. Deploy V2-capable replay
consumers with the new writer. Existing V1 archives continue to load without
conversion. Backend storage treats replay files as opaque blobs; no database
migration is required, and no existing files are rewritten.

Each released version defines a fixed layout. Future wire layout changes require
a new version and compatibility with previously released formats.

## Current V2: fixed-point layout

MemoryPack writes a little-endian signed 32-bit array count, then consecutive
35-byte frames with no padding. The order is **position, velocity, angles, button
masks, MoveType**, grouping the six signed 24-bit components together.

| Offset | Field | Encoding | Bytes |
| --- | --- | --- | --- |
| 0 | Origin X, Y, Z | Three signed int24 values | 9 |
| 9 | Velocity X, Y, Z | Three signed int24 values | 9 |
| 18 | Angles X, Y | Two signed int16 values | 4 |
| 22 | Pressed buttons | Low 32 bits as uint32 | 4 |
| 26 | Changed buttons | Low 32 bits as uint32 | 4 |
| 30 | Scroll buttons | Low 32 bits as uint32 | 4 |
| 34 | MoveType | Byte | 1 |

All spatial components use `integer = round(value * 32)`, with midpoint ties rounded
to even, and decode as `integer / 32`. Signed integers use little-endian two's
complement. The signed high byte of int24 is sign-extended on decode.

| Components | Stored integer range | Decoded range | Step |
| --- | --- | --- | --- |
| Each position/velocity component | −8388608 to 8388607 | −262144 to 262143.96875 | 1/32 |
| Each angle component | −32768 to 32767 | −1024° to 1023.96875° | 1/32° |

Range checks run **after rounding**, before integer conversion. Angles are neither
normalized nor wrapped. This preserves the pre-existing 1/32 quantization precision:
each component's rounding error is at most 1/64 within the V2 range. Integer storage
does not add further quantization; signed floating-point zero becomes integer zero.
The original 64-byte runtime `ReplayFrameData` layout remains unchanged.

V2 saves 29 raw bytes per frame versus V1 (45.3%), excluding the array count and
JSON header. These are raw layout savings, not promised reductions after Zstd.
New writes encode directly into byte buffers; they do not allocate a second array
of compact or quantized frames.

## Button storage

V2 stores only engine bits 0–31 for all three fields: `PressedButtons`,
`ChangedButtons`, and `ScrollButtons`. Bits 32–63 are unused by replay playback and
are intentionally discarded. There is no bit remapping or button-based fallback.

| Engine bit | Stored bit |
| --- | --- |
| 0–31 | Same bit 0–31 |
| 32–63 | Discarded |

Encoding uses `unchecked((uint)(ulong)buttons)` so truncation also works in checked
builds. Decoding zero-extends the stored uint32 into the runtime enum's uint64;
discarded high bits stay zero. The original recording snapshot is not modified.

V1 decoding and spatial-overflow fallback writing retain their full-width layout
and preserve all 64 button bits. V2 is unreleased; the experimental high-bit
remapping layout has no compatibility decoder.

## Buffer ownership and custom serialization

MemoryPack supports custom `MemoryPackFormatter<T>` implementations and low-level
Reader/Writer APIs. Its `SerializeAsync(Stream)` buffers the serialized payload
before writing it. See the [MemoryPack documentation](https://github.com/Cysharp/MemoryPack/blob/main/README.md).
A global formatter for `ReplayFrameData[]` cannot choose the wire layout without
the enclosing replay header, so the replay codec handles this fixed unmanaged-array
contract locally instead. It preserves the exact four-byte count and frame layouts;
this is not a new file format or a change to other MemoryPack consumers.

- The V2 decoder views packed records directly over the validated payload span
  and constructs only the final `ReplayFrameData[]`. V1 copies the raw structs
  directly into that same final array.
- File bytes and Zstd output use `ArrayPool<byte>`. Decoding uses the actual file
  length and actual decompressed byte count, never the rented buffer's capacity.
  The [ZstdSharp span overload](https://github.com/oleg-st/ZstdSharp/blob/0.8.6/src/ZstdSharp/Decompressor.cs)
  writes into caller-owned memory. For frames without a stored content size, its
  size query is an upper bound; validation applies to the actual decoded length.
- Returned frames own their memory. Borrowed buffers are returned in `finally`
  blocks after decoding, and later loads cannot overwrite an earlier replay.
- Local file saves quantize and encode at most 64 KiB per write into a pooled
  scratch buffer, then pass each block to the file or Zstd stream. No whole-payload
  staging array is needed. The JSON header is streamed and has no fixed 4 KiB cap.
- The in-memory upload API still returns an owned, exact-length `byte[]`. Its
  payload and compression scratch buffers are pooled; constructing the returned
  byte array requires one final copy. This path still reserves buffers proportional
  to replay size.
- A save consumes a stable snapshot for its entire duration: the main replay
  detaches the recorded list, and a stage replay materializes a private range.
  The stage snapshot copy remains necessary because recording continues in the
  original list.

Pooling reduces repeated managed allocation; it does not eliminate working memory.
First use or a pool miss may allocate, and the pool can retain large buffers.
Every loaded replay still requires its 64-byte-per-frame runtime array. Dequantizing
a compact frame still does CPU work, once during loading, not during playback.

See [the before/after benchmark](../analysis/replay-low-allocation-benchmark-20260917.md)
for measured allocations and loading times.

## Regression coverage

`Plugin/Timer.Tests/ReplayStorageFormatTests.cs` includes captured V1 files,
an independently specified 35-byte fixture, low-bit preservation and high-bit
truncation for all 64 button positions in each mask, mixed high/low masks,
int24/int16 limits on every component, sign extension, midpoint rounding, range
checks after rounding, non-finite/extreme values, multiple-frame data, both readers,
local and upload writes, compression modes, input immutability, and malformed
headers/payloads. It also checks allocation bounds, asynchronous writes across block
boundaries, large headers, pooled buffer ownership, and corrupt-file backup on Windows.
V2 fixtures load through both readers using only `Version = 2` in the header.

Run `dotnet test Plugin/Timer.Tests/Timer.Tests.csproj -c Release -p:CIBuild=true
-p:CheckForOverflowUnderflow=true -p:TreatWarningsAsErrors=true`.
