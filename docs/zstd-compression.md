# Zstd Compression

ZArchiveSharp includes a complete, dependency-free implementation of the [RFC 8878](https://www.rfc-editor.org/rfc/rfc8878) zstd format — both encoder and decoder. It is a faithful port of libzstd 1.5.7 and produces **byte-identical frames** to `ZSTD_compress(src, level)` for single-shot compression at every level 1–22.

## Compression Levels

ZArchiveSharp supports the full zstd level range, 1–22. Parameters per level come from an exact port of libzstd's `clevels.h` table — no values are invented.

| Level | Strategy | Typical Use |
|-------|----------|-------------|
| 1 | `Fast` | Maximum speed (~1.3 GB/s) |
| 2–3 | `DoubleFast` | Fast with better ratio |
| 4–5 | `Greedy` | Balanced |
| 6–7 | `Lazy` | Default (level 6), good balance |
| 8–9 | `Lazy2` | Better ratio, still fast |
| 10–12 | `BtLazy2` | Ratio-focused |
| 13–15 | `BtOpt` | High compression |
| 16–18 | `BtUltra` | Very high compression |
| 19–22 | `BtUltra2` | Maximum compression (slow) |

### Level Selection Guidelines

- **Level 6 (default)** — Matches `zarchive.exe` behavior; good balance of speed and ratio
- **Levels 1–3** — Real-time or throughput-critical workloads
- **Levels 9–12** — Distribution archives where size matters but time budget exists
- **Levels 19–22** — Archival storage; expect ~38 MB/s at level 19

## Basic Usage

### Single-Shot Compression

```csharp
using ZArchiveSharp.Zstd;

var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));

// Array-based
byte[] frame = compressor.CompressBlock(data);

// Span-based (zero-alloc on the output side)
Span<byte> dest = stackalloc byte[ZstdCompressor.GetCompressBound(source.Length)];
int written = compressor.Compress(source, dest);
if (written == -1)
{
    // Frame would not fit in dest, or is not smaller than the input.
    // Store the input raw — the same rule libzstd's StoreBlock uses.
}
```

### Decompression

```csharp
using ZArchiveSharp.Zstd;

// Array-based with a size cap (required — protects against zip bombs)
byte[] data = ZstdCompressor.DecompressFrame(frame, maxSize: expectedSize);

// Decoder options (defaults: 512 MiB window, 512 MiB frame content)
var options = new ZstdDecoderOptions
{
    MaxWindowSize = 512 * 1024 * 1024,
    MaxFrameContentSize = 512 * 1024 * 1024,
};
```

The decoder handles:
- Standard frames (magic `0xFD2FB528`)
- Skippable frames (magic `0x184D2A5E`) — content ignored
- Multi-frame concatenated input
- XXH64 content checksums (verified when present)
- RLE and raw block types

## Compression Strategies

The nine strategies map directly to libzstd's match finders:

| Strategy | Port Source | Notes |
|----------|-------------|-------|
| `Fast` | `zstd_fast.c` | Single hash table |
| `DoubleFast` | `zstd_double_fast.c` | Two hash tables (long + short matches) |
| `Greedy` | `zstd_lazy.c` (depth 0) | No lazy match extension |
| `Lazy` | `zstd_lazy.c` (depth 1) | Checks one position ahead |
| `Lazy2` | `zstd_lazy.c` (depth 2) | Checks two positions ahead |
| `BtLazy2` | `zstd_opt.c` (binary tree) | Binary-tree search + optimal parse |
| `BtOpt` | `zstd_opt.c` | Optimal parse |
| `BtUltra` | `zstd_opt.c` | Deeper search |
| `BtUltra2` | `zstd_opt.c` | Two-pass seeding |

Strategy is selected automatically by level — you cannot set it independently (matching libzstd behavior, where strategies come from the level table).

## Size Tiers

libzstd selects compression parameters not just by level but by **input size**. ZArchiveSharp ports this exactly (`ZstdCompressionParameters.ForSizeAndLevel`):

| Tier | Input Size |
|------|-----------|
| `Le16K` | ≤ 16 KiB |
| `Le128K` | ≤ 128 KiB (ZAR 64 KiB blocks land here) |
| `Le256K` | ≤ 256 KiB |
| `Default` | > 256 KiB |

For multi-block frames, the parameter row is derived **once** from the total input size and shared by every block — exactly like `ZSTD_getCParams` on the pledged size.

## Frame Structure

Frames ZArchiveSharp writes follow RFC 8878 §3:

```
+-------------+--------+--------+--------+--------+
| Magic       | Header | Block  | Block  | ...    |  (+ optional checksum)
| 4 bytes     | 3-13 B | Header | Data   |        |
| FD2FB528    |        |        |        |        |
+-------------+--------+--------+--------+--------+
```

- **Magic**: `0xFD2FB528` (little-endian)
- **Frame header**: explicit window descriptor, size-dependent frame content size
- **Blocks**: up to 128 KiB each (`ZSTD_BLOCKSIZE_MAX`), compressed / raw / RLE types
- **Checksum**: 4-byte XXH64 when `ChecksumFlag` is set (default off)

### Key Invariants

- Blocks below 7 bytes go raw without attempting compression (libzstd's `MIN_CBLOCK_SIZE` rule)
- Raw fallback never advances the repeat-offset history (RFC 8878 §4.1.1 frame-scoped repcodes)
- Blocks that compress to ≥ input size are stored raw
- Repeat-offset history is frame-scoped and restored correctly across raw blocks

## Block Splitting (High Levels)

At levels with `windowLog ≥ 17` using optimal parsers (btopt and above), libzstd may split 128 KiB blocks into sub-blocks using an entropy-estimation search to squeeze extra ratio. ZArchiveSharp ports this:

- `ZstdBlockSplitter` — recursive entropy-estimation search (`ZSTD_deriveBlockSplits`)
- Per-partition offset-code resolution with dual repcode histories
- Post-parse splitting by estimated entropy cost
- Pre-splitting of heterogeneous 128 KiB inputs via raw-byte fingerprint (`ZSTD_optimalBlockSize` / `ZSTD_splitBlock`)

This is why byte parity holds across the full multi-block matrix (636 vectors, levels 1–22).

## Interop

### With Standard zstd Tools

Frames ZArchiveSharp produces decode with:
- The official `zstd` CLI
- Any RFC 8878-conformant decoder

And ZArchiveSharp decodes anything standard tools produce (levels 1–22, with or without dictionaries, no legacy frames).

### Verification

The test suite proves byte-identity:
- **`ParityVsNativeLevelsTests`** — levels 1–22 vs `zstd` CLI
- **`ParityVsNativeMultiblockTests`** — 636-vector multi-block matrix
- **`ZstdGoldenTests`** — 12 committed libzstd 1.5.7 one-shots (no toolchain needed in CI)
- **`ZstdInteropVectorsTests`** — decode vectors produced by libzstd

## Known Boundaries

1. **zarchive.exe bundles libzstd 1.5.2** — its level 6 differs from 1.5.7 on some multi-transition heterogeneous 64 KiB blocks. ZArchiveSharp follows the frozen 1.5.7 reference. Homogeneous blocks and single-transition blocks are identical; extract interops both ways regardless.

2. **Dictionary use only, no training** — `ZstdDictionary.FromBytes`
   (auto-detects formatted vs raw prefix) and `FromRawPrefix` supply history
   (+ initial tables for formatted dicts) to the compressor
   (`ZstdCompressionOptions.Dictionary`), the decompressor
   (`ZstdDecompressor.Decompress(..., dict)`), and both stream wrappers.
   A supplied dictionary is always active per frame; frames carrying a
   dictionary ID require an ID match. Dictionary content counts toward
   `ZstdDecoderOptions.MaxWindowSize`. Training (`COVER`/`DictBuilder`),
   LDM, and multithreading stay out. Dict frames decode in stock `zstd -D`
   (verified against libzstd 1.5.7 goldens); byte-identity with `zstd -D`
   output is a goal, not a guarantee.

3. **No multi-threaded compression** (`zstdmt`) inside one frame — use the pipeline's batch parallelism instead (archives/frames compress independently).

4. **Stream wrappers buffer, then emit** — `ZstdCompressionStream` buffers input
   and emits one unknown-size-header frame at `Dispose()` (byte-identical to a
   single-shot encode of the same bytes); `Flush()` emits the 6-byte header
   early. `ZstdDecompressionStream` decodes incrementally with the
   `ZstdDecoderOptions` caps enforced. Neither type seeks.

## Performance Notes

Current baseline (net10.0, Release — see [Benchmarks](benchmarks.md)):

| Operation | Throughput |
|-----------|-----------|
| Level 1 compress | ~1.3 GB/s |
| Level 6 compress | ~450–470 MB/s |
| Level 19 compress | ~38 MB/s |
| Decode | ~860 MB/s |

Measured hot-path speed is at native parity (L6 64 KiB ≈1.0× libzstd 1.5.7, decode ≈1.0× — see [Benchmarks](benchmarks.md)). The standing trade-off is byte-exact parity and safe (no `unsafe`) code over squeezing the last microseconds; the tracked levers are ArrayPool rental of the per-frame bound-size buffer and span-specializing the hot match-finder loop.
