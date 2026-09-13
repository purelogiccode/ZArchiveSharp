# ZAR Format Specification

The `.zar` format is ZArchive 0.1.2 by Exzap ([unknownbrackets/ZArchive](https://github.com/unknownbrackets/ZArchive)). This page documents the on-disk layout as implemented by ZArchiveSharp, which is byte-identical to the reference implementation.

## Overview

A `.zar` file stores a **directory tree** whose file contents are split into **64 KiB blocks**, each independently compressed with zstd. The layout:

```
+---------------------------+
| Compressed data blocks    |  <- zstd frames, back to back
+---------------------------+
| Offset records            |  <- block size index
+---------------------------+
| Name table                |  <- deduplicated entry names
+---------------------------+
| File tree                 |  <- directory entries
+---------------------------+
| Meta directory (empty)    |
+---------------------------+
| Meta data (empty)         |
+---------------------------+
| Footer (144 bytes)        |  <- section table + SHA-256
+---------------------------+
```

All integers are **big-endian**. Entry names are encoded in **Windows-1252**.

## Constants

| Constant | Value | Meaning |
|----------|-------|---------|
| Block size | 64 KiB (`0x10000`) | Uncompressed block granularity |
| Entries per offset record | 16 | Blocks indexed per record |
| Invalid node | `0xFFFFFFFF` | Path-not-found handle |
| Root name offset | `0x7FFFFFFF` | Root dir has no name |
| Max file size | 2⁴⁸ − 1 | Per-file limit |
| Max name length | `0x7FFF` | 15-bit length header |
| Footer magic | `0x169F52D6` | Last u32 |
| Version | `0x61BF3A01` | Second-to-last u32 |

## Compressed Data

File content is cut into 64 KiB blocks. Each block is compressed independently:

- **Compressed**: a standalone zstd frame (windowLog = 17), one frame per block
- **Raw**: stored uncompressed when compression does not shrink the block (the reference `StoreBlock` rule: compressed ≥ input → raw)
- **Padding**: the final partial block is **zero-padded to 64 KiB** before compression

Repeat-offset history is **frame-scoped** — each block is an independent frame, so repcodes never carry across blocks.

## Offset Records

Each record indexes 16 blocks (40 bytes on disk):

```
struct CompressionOffsetRecord      // 40 bytes, BE
{
    uint64 baseOffset;              // output offset of first block in record
    uint16 sizes[16];               // per-block (compressedSize - 1)
}
```

A stored `sizes[i]` of `0xFFFF`-decoding logic applies: actual compressed size = `sizes[i] + 1`. Blocks are located by:

```
blockIndex -> record = blockIndex / 16, slot = blockIndex % 16
blockOffset = record.baseOffset + sum(sizes[0..slot-1] + 1)
```

The records form a contiguous table placed after the compressed data.

## Name Table

Deduplicated entry names, each stored as:

```
uint16 length;                      // up to 0x7FFF
byte   chars[length];               // Windows-1252
```

Names longer than `0x7FFF` characters are truncated on **characters** before encoding (not bytes) — a faithful port of a 0.1.2 quirk.

Windows-1252 decoding maps `0x80–0x9F` through the CP1252 high table (undefined slots map to C1 controls); characters with no representation encode to `?` (`0x3F`).

Name order is pack order (first appearance) by default; `ZArchiveWriter` accepts a pre-seeded order (`ZarPipelineOptions.NameOrder`) so callers that walk a source tree in discovery order can emit the table in that order instead.

## File Tree

A flat array of 16-byte entries, serialized in preorder:

```
struct FileDirectoryEntry           // 16 bytes, BE
{
    uint32 nameOffsetAndTypeFlag;   // bit 31: file flag; bits 0-30: name offset
    uint32 field1;
    uint32 field2;
    uint32 field3;
}
```

### File Entry

| Field | Meaning |
|-------|---------|
| `field1` | File offset low 32 bits |
| `field2` | File size low 32 bits |
| `field3` | bits 0–15: offset high 16; bits 16–31: size high 16 |

Offset and size are 48-bit values assembled from the split fields. Offsets are **logical (uncompressed) input offsets**.

### Directory Entry

| Field | Meaning |
|-------|---------|
| `field1` | First child node index |
| `field2` | Child count |
| `field3` | Reserved (0) |

The root directory is entry 0 with the special name offset `0x7FFFFFFF`.

## Footer

144 bytes, placed at the end:

```
struct Footer                       // 144 bytes, BE
{
    OffsetInfo compressedData;      // 16 bytes: uint64 offset + uint64 size
    OffsetInfo offsetRecords;       // 16
    OffsetInfo names;               // 16
    OffsetInfo fileTree;            // 16
    OffsetInfo metaDirectory;       // 16 (empty in 0.1.2)
    OffsetInfo metaData;            // 16 (empty in 0.1.2)
    byte     integrityHash[32];     // SHA-256
    uint64   totalSize;             // including footer
    uint32   version;               // 0x61BF3A01
    uint32   magic;                 // 0x169F52D6  <- magic/version at the END
}
```

### Integrity Hash

The SHA-256 covers **every output byte written before the footer**, then the footer itself with the hash field zeroed. This detects truncation and most corruption; the reader rejects archives whose hash does not verify.

## Path Semantics

- Separators: both `/` and `\` accepted on input; stored paths use `/`
- Lookup is **case-insensitive** (ASCII A–Z folding only)
- Name deduplication is by **Windows-1252 bytes** (v1.2.0): writer names are
  truncated to 0x7FFF chars and encoded before identity/sort decisions, so
  names that differ only in characters CP1252 cannot represent collapse to
  one node exactly like the C++ tool; the file-tree sort uses the same byte
  comparator as the native reader
- Sort order mirrors the C++ comparator exactly, including its reversed-mismatch quirk (`(byte)c2 - (byte)c1`) and "shorter string sorts after its prefix"

## Reader Behavior

- `TryOpen` returns `null` on **any** validation failure — it never throws (mirrors the C++ open chain); with `leaveOpen: false` (the default), a failed `TryOpen(Stream)` also disposes the stream, so ownership only transfers on success. The `out ZArchiveOpenFailure` overloads report the specific reason (`BadMagic`, `LengthMismatch`, `SectionOutOfRange`, …)
- A **4 MiB LRU cache** (64 × 64 KiB blocks, configurable via `ZArchiveReaderOptions.CacheBlockCount`) holds decompressed blocks
- Reads are thread-safe: cache bookkeeping and copies take the lock, while block decompression runs outside it, so distinct blocks decode in parallel
- Directory enumeration can return child node handles (`TryGetDirEntry`) and canonical names (`TryGetNodeName`), and `GetDirEntryCount` clamps crafted counts to the file-tree bounds
- `OpenRead`/`TryOpenRead` expose a seekable per-entry stream over the block cache
- Data blocks carry no per-block checksums (same as native): flipped bytes may decode to different content instead of throwing. Truncations always fail the open.
- Crafted tables are bounds-checked without wrapping (`OffsetInfo.IsWithinValidRange`, child ranges, directory indices); a block fault mid-read returns a short read instead of looking like EOF
- Extraction treats entry names as untrusted: traversal, rooted, drive-qualified, and reserved device names are refused, and the resolved path must stay under the destination root (see [Pipeline](pipeline.md#extraction-safety))

## Limits

| Limit | Value |
|-------|-------|
| File size | 2⁴⁸ − 1 bytes |
| Name length | 0x7FFF chars |
| Block count | ~2⁶⁴ blocks (offset records are 64-bit based) |

## Compatibility

- ZArchiveSharp-written archives extract with the original `zarchive.exe` and vice versa (tested both directions)
- Byte-parity is at the **writer API sequence** level: filesystem enumeration order is OS-unspecified in C++, so `ZArchiveTool.Pack` sorts entries by default (`deterministicOrder: true`); pass `false` to mirror native enumeration for parity runs
