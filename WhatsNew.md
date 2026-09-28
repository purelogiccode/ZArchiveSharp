# What's New in v1.3.0

**Versioning:** package versions derive from git tags via MinVer
(`v`-prefixed annotated tags). Tagging `v1.3.0` stamps 1.3.0 on the
`ZArchiveSharp` library, the `ZArchiveSharp.Cli` (`zar`) tool, the test
projects and the benchmarks together.

**Provenance:** Release build, `0` warnings / `0` errors;
`4291/4291` library tests + `43/43` CLI battle tests green (net10.0). The CI
matrix repeats the same suites on Ubuntu/Windows/macOS when the tag is pushed.

**License:** **MIT.** See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Highlights

### Mount-friendly random-access reader API
The reader now exposes the surface a virtual file system or mount host needs,
without rebuilding paths or re-parsing the tree:

- **Node handles everywhere.** `ZArchiveReader.RootNode` (`0`) names the
  root, `LookUp` resolves paths, and the new
  `TryGetDirEntry(node, index, out childNode, out entry)` enumerates a
  directory *and* returns the child handle. `GetDirEntryCount` is clamped to
  the file-tree bounds, so a crafted directory entry can never make callers
  iterate past the table.
- **Canonical names.** `TryGetNodeName(node, out name)` returns the stored
  (original-casing) name; the root's name is `""`.
- **Seekable per-entry streams.** `OpenRead(node)` and
  `TryOpenRead(path)` return a seekable, read-only `Stream` over a file's
  uncompressed contents, reading through the block cache. Disposing the
  stream does not dispose the archive.
- **Archive stats at open.** `EntryCount` (file-tree entries) and
  `TotalUncompressedSize` (natural "volume size") are computed from the
  in-memory tree, with no archive I/O. The total saturates at
  `ulong.MaxValue` for crafted sizes that would otherwise overflow.

### Specific open-failure reasons
`ZArchiveOpenFailure` plus new `out` overloads for the path, stream and
byte-array opens report exactly which check failed instead of a bare `null`:
`FileNotFound`, `AccessDenied`, `InvalidStream`, `ReadError`, `TooSmall`,
`BadMagic`, `UnsupportedVersion`, `LengthMismatch`, `SectionOutOfRange`,
`BadOffsetRecords`, `BadNameTable`, `BadFileTree`, and `InvalidPath` (null,
empty, or invalid path characters). The plain `TryOpen` overloads are
unchanged and still return `null` on any failure.

### Options for mount hosts
`ZArchiveReaderOptions` tunes an open: `CacheBlockCount` (default 64 blocks =
4 MiB; raise it for many concurrent streams), `DecodeExtendedNames` (see
below), and `FileShare` for path opens (use `FileShare.ReadWrite` so
scanners and indexers can keep the archive open while it is mounted).

### Parallel block decode
`ReadFromFile` now decompresses blocks **outside** the global lock: cache
bookkeeping and copies stay locked, distinct 64 KiB blocks decode in
parallel, and the preallocated LRU buffers are preserved. Concurrent reads
of separate files no longer serialize on decompression.

### Corrected extended-name decoding (opt-in)
The 0.1.2 name-table quirk that makes names of ≥ 0x80 characters decode to
`""` is still the default (byte parity). Opening with
`DecodeExtendedNames = true` — or calling the new
`GetName`/`GetNameRaw(..., decodeExtendedLengths: true)` overloads — decodes
those names correctly, so long-named entries become listable, resolvable and
readable.

### Crafted-archive hardening and fixes
Following review of the new surface:

- **Extraction still fails loudly on crafted directory ranges.**
  `GetDirEntryCount` clamps for enumeration, but extraction uses the raw
  stored count and rejects a child range that runs past the file tree
  (`Directory contains invalid node.`) instead of silently extracting a
  partial directory.
- **`InvalidPath`** is reported for null/empty/invalid paths instead of the
  misleading `FileNotFound`.
- **`TotalUncompressedSize` saturates** instead of wrapping on crafted trees.
- Long-name entries are documented as counted by `GetDirEntryCount` but
  rejected by `GetDirEntry`/`TryGetDirEntry` until `DecodeExtendedNames` is
  enabled.

## Upgrade notes

- **Additive API; no wire-format changes.** Archive bytes, zstd frame bytes
  and CLI exit codes are unchanged, and the existing reader overloads keep
  their behavior. Recompiling is enough; no data migration is needed.
- **Behavior changes only for invalid or crafted input:** empty/invalid paths
  now report `InvalidPath`; directory enumeration clamps out-of-range child
  counts; extraction rejects crafted child ranges; the archive total
  saturates on overflow. Valid archives are unaffected.
- **License:** MIT.

## Full change list since v1.2.2

- `feat:` mount-host reader APIs — `RootNode`, `TryGetDirEntry`,
  `TryGetNodeName`, clamped `GetDirEntryCount`, `EntryCount`,
  `TotalUncompressedSize`, `OpenRead`/`TryOpenRead`
- `feat:` `ZArchiveOpenFailure` enum and failure-reporting `TryOpen` overloads
  for path, stream and byte-array opens
- `feat:` `ZArchiveReaderOptions` (`CacheBlockCount`, `DecodeExtendedNames`,
  `FileShare`) and corrected extended-name decode overloads
- `feat:` `ReadFromFile` decompresses outside the global lock so distinct
  blocks decode in parallel (lock-free decode, locked publish)
- `fix:` extraction fails loudly on crafted directory child ranges
- `fix:` null/empty/invalid paths report `InvalidPath`, not `FileNotFound`
- `fix:` `TotalUncompressedSize` saturates instead of wrapping
- `test:` mount-host reader API tests and a crafted-count extraction
  corruption test
- `style:` analyzer cleanups in the mount API tests
- `docs:` API reference, format, FAQ and README updates for the new surface
