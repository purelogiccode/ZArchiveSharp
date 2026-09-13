# Release Notes

Version history for ZArchiveSharp and the `zar` CLI. Package versions derive
from `v`-prefixed git tags via MinVer; the library and CLI release together.
The detailed notes for the current release live in
[WhatsNew.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/WhatsNew.md).

## v1.2.0

**Highlights**

- **CLI telemetry, logging, and update checks (opt-out).** Serilog-backed
  logging, bug-report forwarding, anonymous usage stats, and a background
  GitHub release check. Disable everything with `--no-telemetry` or
  `ZAR_BUG_REPORT=off`; `--help`/`--version` never send. Telemetry flush is
  bounded (<1 s) and never delays exit.
- **Extraction security.** Zip-slip validation (traversal, absolute,
  drive-qualified, and Windows device names), resolved-path re-validation,
  a 1024-level nesting cap, scratch-file writes moved into place after the
  size check, and no descending through directory symlinks/junctions.
- **Argument handling.** Unknown options are `-1`; `--` ends option parsing
  everywhere (including inside `zar zstd`/`zar seekable`); value options no
  longer swallow the next flag while dashed values remain usable; `-c`
  before or after `zstd` means compress. Batch runs report `-11` when the
  only failures are collision refusals and `-13` for missing 7z/unreadable
  batch input.
- **Correctness.** CP1252 name identity/order matches the C++ tool; splitter
  repeat history matches native; frame headers survive staging-buffer
  compaction; skippable payloads are streamed, not buffered; `SeekableReader`
  range errors are clean `-12` failures (not crashes); process-runner
  stderr/kill handling hardened; delete-source runs only after the terminal
  `.zar` succeeds.
- **New APIs.** `ZstdDecoderOptions.MaxTotalOutputSize` (1 GiB default),
  `ZarPackEngine.MoveIntoPlace`, `ZarPackEngine.OutputExistsMessage`,
  `ZarPackEngine.MaxExtractDepth`; `ZarPackEngine.PackEntries` returns the
  written path and takes a collision policy; `PauseTokenSource` is
  `IDisposable`.

**Upgrade notes**

- Level-6 output stays byte-identical, and CP1252-representable archive
  names are unchanged; names with unrepresentable characters now collapse
  like the C++ tool.
- `SeekableReader.DecompressRange`/`DecompressFrames` now throw
  `ArgumentOutOfRangeException` for range errors (was `ZstdException`).
- `ZArchiveReader.TryOpen(Stream)` disposes the stream when the open fails
  (with `leaveOpen: false`, the default).
- Telemetry is opt-out; no data is sent for `--help`/`--version` launches.

Full notes:
[WhatsNew.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/WhatsNew.md).

## v1.1.0

**Highlights**

- **Block-level parallelism** for a single pack/extract:
  `ZarPipelineOptions.MaxDegreeOfParallelism` fans 64 KiB block
  compression/decompression across workers, byte-identical to sequential
  output. Direct `ZArchiveWriter` use stays sequential unless a
  `compressorFactory` is supplied.
- **Codec scratch pooling** (match tables, frame copies, sequence stores)
  reduces GC pressure on large packs.
- **CLI positional contract** matches the oracle: exactly `input [output]`;
  `-o` occupies the output slot; `--iso` takes the output positionally or via
  `-o`, never both; extras fail with `-1`/`Too many paths specified`.
- **CLI battle-test suite** (`ZArchiveSharp.CliBattleTests`) locks exit
  codes, stdout, and cross-interop against the reference `zarchive.exe`
  when it is present.
- **Packaging:** XISOSharp via NuGet (`1.0.2`), single-file bundle named
  `ZArchiveSharp.exe`.

## v1.0.2 and earlier

Pre-release-notes versions established the ZArchive 0.1.2 container port,
the full RFC 8878 zstd codec (levels 1–22, all 9 strategies), the seekable
zstd format, the pipeline engine, and the initial `zar` CLI. See the git tags
(`v1.0.0`, `v1.0.1`, `v1.0.2`) for the corresponding sources.
