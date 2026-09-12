# Seekable Format

ZArchiveSharp implements the seekable zstd format (spec v0.1.1, Foot + Head) as used by [zeekstd](https://github.com/rorosen/zeekstd) and the original C [seekable format](https://github.com/facebook/zstd/tree/dev/contrib/seekable_format) — independently compressed frames plus a skippable-frame seek table that enables **random access** without decoding the whole stream.

## Why Seekable?

Standard zstd frames must be decoded from the start. Seekable files split the payload into independent frames (default 2 MiB uncompressed each) and index them, so reading any byte range decodes **only the frames it touches**:

```
+--------+--------+--------+--------+-----+-----------+
| Frame0 | Frame1 | Frame2 | Frame3 | ... | SeekTable |
+--------+--------+--------+--------+-----+-----------+
                                             (skippable frame)
```

Typical uses: large datasets queried by offset, video/indexed media containers, database pages, cloud-random-access objects.

## Writing

### Basic

```csharp
using ZArchiveSharp.Seekable;

var writer = new SeekableWriter();           // defaults: level 3, 2 MiB frames, checksums on
writer.Write(chunk1);
writer.Write(chunk2);
byte[] file = writer.Finish();               // complete seekable file (Foot)
```

### Options

```csharp
using ZArchiveSharp.Seekable;

var writer = new SeekableWriter(new SeekableOptions
{
    Level = 9,                               // zstd level 1..22 (default 3)
    FrameSize = 8 * 1024 * 1024,             // frame threshold (default 2 MiB)
    Policy = SeekableFrameSizePolicy.Uncompressed,  // or .Compressed
    Checksum = true,                         // XXH64 per frame (default true)
});
```

| Option | Default | Meaning |
|--------|---------|---------|
| `Level` | 3 | zstd compression level (1–22) |
| `FrameSize` | 2 MiB | Frame threshold; capped at 1 GiB (`MaxFrameSize`) |
| `Policy` | `Uncompressed` | What `FrameSize` measures |
| `Checksum` | `true` | 4-byte XXH64 content checksum per frame |

### Frame Size Policies

Port of zeekstd's `FrameSizePolicy`:

- **`Uncompressed`** (default) — a frame ends exactly at `FrameSize` uncompressed bytes. Predictable boundaries; best for random access.
- **`Compressed`** — a frame ends once its compressed size reaches the threshold, checked at 128 KiB input granularity. Better ratio behavior with highly compressible data; boundaries may shift relative to the oracle when output-buffer refills land mid-chunk (both outputs remain valid).

Either way, a new frame always starts at 1 GiB of uncompressed data (`SEEKABLE_MAX_FRAME_SIZE`).

### Head vs Foot

```csharp
// Foot: seek table appended to the file (standard, self-describing)
byte[] file = writer.Finish();

// Head: standalone seek table, delivered out-of-band (sidecar, header row, ...)
(byte[] frames, byte[] seekTable) = writer.FinishHead();
```

The standalone Head is useful when the table must precede the data (e.g., an object-storage header) or when appending data after the table was produced.

## Reading

### Open and Query

```csharp
using ZArchiveSharp.Seekable;

var reader = new SeekableReader(file);       // parses the embedded Foot table

Console.WriteLine(reader.FrameCount);        // number of frames
Console.WriteLine(reader.DecompressedLength); // total uncompressed size
Console.WriteLine(reader.Table);             // full seek table
```

Throws `ZstdException` when no valid seek table is present.

### External (Head) Tables

```csharp
// Data and table supplied separately
var reader = new SeekableReader(frameBytes, seekTable);
```

### Decompress

```csharp
// Whole payload
byte[] all = reader.DecompressAll();

// Arbitrary range: decodes only the frames the range touches
byte[] slice = reader.DecompressRange(offset: 5_000_000, length: 100_000);

// Whole frames, first..last inclusive, concatenated
byte[] frames = reader.DecompressFrames(2, 5);
```

Mid-frame starts decompress from the frame start (like zeekstd's dummy decompression up to the offset) — correctness is identical, only the touched frames cost time.

### Seek Table Binary Search

`SeekTable.FrameIndexAtDecomp(offset)` locates the frame containing a decompressed offset via binary search over frame boundaries.

## Format Details

### Seek Table (Skippable Frame)

The table is a standard zstd **skippable frame** (magic `0x184D2A5E`) — decoders that ignore skippable frames can still stream the file linearly.

```
Skippable frame header
  magic          4B  5E 2A 4D 18 (LE)
  frame size     4B  (LE)
--- Seek_Table_Entries payload ---
  head/beacon    4B  0x8F92EAB1 (LE) — integrity marker
  # frames       4B  (LE)
  entries:
    cSize        4B  compressed frame size
    dSize        4B  decompressed frame size
  (legacy checksums present in old files are parsed and ignored;
   reserved bits are rejected)
```

Two placement flavors are supported:

- **Foot** — table after the last frame (zeekstd CLI default)
- **Head** — table without frame data, or parsed standalone

### Frame Bytes

Per-frame bytes are streaming-style, byte-identical to real libzstd streaming (verified against a gcc oracle over actual libzstd):

- Unknown-size parameter row (row 0), content-size flag 0 headers
- Content that is an exact non-zero multiple of 128 KiB ends with an **empty last block** (the `ZSTD_writeEpilogue` rule)
- Empty input emits one empty frame (the oracle's unconditional trailing `end_frame`)
- `Checksum = true` sets the frame content-checksum flag — **the zeekstd flavor**; the C reference library writes plain frames (also valid; our reader decodes both)

### Maximums

| Limit | Value |
|-------|-------|
| Frame size (uncompressed) | 1 GiB (`SEEKABLE_MAX_FRAME_SIZE`) |
| Frame count | 134,217,728 (`0x08000000`, `SEEKABLE_MAX_FRAMES`) |

## Compatibility

- Files written by ZArchiveSharp decode with the `zeekstd` CLI and the C seekable format reference
- Stock `zstd` ignores the skippable table and decodes the frame stream linearly
- Files produced by either reference flavor (plain frames, checksummed frames) decode here
- Verified by 585 seekable tests, including 248 byte-identity vectors vs a gcc oracle over real libzstd streaming (levels 1–19, Foot + Head + Compressed policy)
