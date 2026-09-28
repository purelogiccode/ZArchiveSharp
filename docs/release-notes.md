# Release Notes

Version history for ZArchiveSharp and the `zar` CLI. Package versions derive
from `v`-prefixed git tags via MinVer; since v1.2.2 every project in the
solution (library, CLI, tests, benchmarks) releases together. The detailed
notes for the current release live in
[WhatsNew.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/WhatsNew.md).

> **License:** ZArchiveSharp is distributed under the
> [MIT License](https://github.com/purelogiccode/ZArchiveSharp/blob/master/LICENSE).
> Third-party notices:
> [THIRD-PARTY-NOTICES.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/THIRD-PARTY-NOTICES.md).

## v1.4.0

**Highlights**

- **Original, MIT-licensed pipeline layer.** `ZarPipeline` and the engine
  behind it (`ZarPackEngine`, `ProcessRunner`, `SevenZip`,
  `ProcessableFiles`, `ZarSettings`, the stage/progress model and the CLI
  batch orchestration) are now an independent implementation. The public
  surface is unchanged — same types, members, progress semantics, collision
  policies and exit codes — but the repository no longer contains code
  derived from non-MIT sources. All of ZArchiveSharp is MIT.
- **Stricter per-item batch isolation.** A source path that cannot be mapped
  to an output (invalid path characters, for example) now fails only its own
  item as a `Failed` `ZarItemResult` instead of throwing
  `AggregateException` out of `PackBatch`/`ExtractBatch`.
- **Robust process runner.** A tool whose stderr pipe is inherited and held
  open by a grandchild no longer stalls the bounded drain wait; the pump is
  stopped and the already-read lines are kept.
- **CI as the gate.** GitHub Actions builds and tests on Ubuntu, Windows and
  macOS, packs both NuGet packages, and publishes on `v*` tags. MinVer now
  sees full history, and the CLI harnesses fold POSIX exit statuses and run
  cross-platform.

**Upgrade notes**

- No API, wire-format or CLI changes since v1.3.0; no data migration.
- NuGet packages use the SPDX expression `PackageLicenseExpression=MIT`;
  `LICENSE` and `THIRD-PARTY-NOTICES.md` stay in the packages.
- Behavior changes apply only to malformed input (unmappable batch paths,
  held stderr pipes).

Full notes:
[WhatsNew.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/WhatsNew.md).

## v1.3.0

**Highlights**

- **Mount-friendly reader API.** Node-handle directory walks
  (`RootNode`, `TryGetDirEntry`), canonical names (`TryGetNodeName`),
  seekable per-entry streams (`OpenRead`/`TryOpenRead`), and archive stats
  (`EntryCount`, `TotalUncompressedSize`) for virtual file systems and other
  random-access hosts.
- **Specific open-failure reasons.** `ZArchiveOpenFailure` plus `out`
  overloads for path/stream/byte-array opens report `BadMagic`,
  `UnsupportedVersion`, `LengthMismatch`, `SectionOutOfRange`,
  `BadOffsetRecords`, `BadNameTable`, `BadFileTree`, `TooSmall`,
  `InvalidStream`, `FileNotFound`, `AccessDenied`, `ReadError` and
  `InvalidPath` instead of a bare `null`.
- **Reader options.** `ZArchiveReaderOptions` adds `CacheBlockCount`
  (default 64 × 64 KiB), opt-in corrected extended-name decoding
  (`DecodeExtendedNames`), and `FileShare` for path opens.
- **Parallel block decode.** `ReadFromFile` decompresses distinct 64 KiB
  blocks outside the global lock (cache bookkeeping and copies stay locked),
  so concurrent reads scale; the preallocated LRU buffers are preserved.
- **Hardening fixes.** Extraction still rejects crafted directory child
  ranges loudly; null/empty/invalid paths report `InvalidPath`;
  `TotalUncompressedSize` saturates instead of wrapping.

**Upgrade notes**

- Additive API: no wire-format or CLI behavior changes, no breaking changes
  to existing reader overloads.
- Behavior changes apply only to invalid or crafted input: `InvalidPath` for
  bad paths, clamped enumeration counts, loud extraction failure on crafted
  child ranges, and a saturating archive size.

## v1.2.2

**Highlights**

- **CLI help names the executable you launched.** Usage, help, error text and
  `--version` print `ZArchiveSharp` from the standalone bundles and `zar`
  from the global tool (falling back to `zar` for
  `dotnet ZArchiveSharp.Cli.dll`). The substitution is token-aware, so the
  `.zar` extension and `zstd` are unaffected.
- **Windows app icon and metadata.** The CLI executable now embeds the
  ZArchiveSharp icon (`ApplicationIcon`) and `Company =
  PureLogicCode.com`; Unix bundles are unaffected.
- **Tag-driven versioning for every project.** MinVer moved to
  `Directory.Build.props`, so the library, CLI, tests, battle tests and
  benchmarks all stamp `1.2.2`.
- **Standalone release bundles** for win/linux/osx × x64/arm64
  (`release_1.2.2_<rid>.zip`): single-file, framework-dependent (no .NET
  runtime embedded; .NET 10 runtime required), with the
  `ZArchiveSharp.Cli.runtimeconfig.json` sidecar, README, LICENSE and
  THIRD-PARTY-NOTICES, and executable bits set on Unix.

**Upgrade notes**

- No wire-format or API changes since v1.2.0.
- Standalone bundles are framework-dependent: install the matching .NET
  runtime and keep the runtimeconfig sidecar next to the executable.

## v1.2.1

**Highlights**

- **License.** ZArchiveSharp is distributed under the
  [MIT License](https://github.com/purelogiccode/ZArchiveSharp/blob/master/LICENSE).
- Added [THIRD-PARTY-NOTICES.md](https://github.com/purelogiccode/ZArchiveSharp/blob/master/THIRD-PARTY-NOTICES.md)
  with the permissive notices (ZArchive MIT-0, libzstd BSD-3, zeekstd BSD-2,
  seekable-zstd MIT, ZstdSharp MIT), and the XGDTool (GPL-3.0, not
  incorporated) note.
- NuGet packages now ship `<license type="file">LICENSE</license>` and
  include the notices file.

**Upgrade notes**

- No binary or API changes.

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
