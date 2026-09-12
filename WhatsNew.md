# What's New in v1.2.0

**Versioning:** package versions derive from git tags via MinVer
(`v`-prefixed annotated tags). Tagging `v1.2.0` stamps 1.2.0 on the
`ZArchiveSharp` library and the `ZArchiveSharp.Cli` (`zar`) tool together.

**Provenance:** Release build, `0` warnings / `0` errors;
`4279/4279` library tests + `43/43` CLI battle tests green
(Ubuntu/Windows/macOS via `.github/workflows/ci.yml`).

## Highlights

### CLI telemetry, logging, and update checks (opt-out)
Every CLI line now flows through Serilog. Warnings and errors are also
forwarded to the PureLogicCode bug-report API (environment, error, and
exception details; at most 9 reports/minute; user profile and account name
redacted; API key obfuscated in the binary), each launch records one
anonymous usage hit, and a background GitHub release check announces newer
versions with an optional browser prompt.

- `--no-telemetry` or `ZAR_BUG_REPORT=off` disables **all** outbound calls;
  `--help`/`--version` launches never send anything.
- Telemetry never delays exit: the bug-report/usage flush is bounded to
  well under a second and runs behind the command, not in front of it.
- The update prompt is bounded (15 s key poll), and a double-clicked
  console window is held open so the output can be read.

### Extraction and pack hardening
- **Zip-slip protection:** extracted names must be single plain components;
  `..`, absolute, drive-qualified, and reserved device names are rejected,
  and the resolved path is re-validated against the destination root.
- **Bounded nesting:** archives nested deeper than `ZarPackEngine.MaxExtractDepth`
  (1024) fail catchably instead of risking a stack overflow.
- **No truncated outputs:** each file is written through a unique `.part`
  scratch file and moved into place only after the size check passes.
- **No symlink traversal:** packing never descends directory
  symlinks/junctions (reparse points); the link itself is kept as an empty
  directory entry.
- **Decoder caps:** `ZstdDecoderOptions.MaxTotalOutputSize` (default 1 GiB)
  bounds concatenated frames cumulatively; the seekable writer enforces the
  1 GiB uncompressed frame cap in both policies; huge skippable frames are
  skipped without buffering their payload.

### CLI argument handling and exit codes
- Unknown options are `-1` usage errors instead of silently becoming paths.
- `--` ends option parsing — for the plain pack/extract shape **and** inside
  `zar zstd` / `zar seekable`, so dashed paths stay reachable everywhere.
- Value-taking options no longer swallow the following flag
  (`zar --jobs --quiet in out` errors), but dashed values stay usable
  (`zar -o -out.zar src`, `zar --iso -game.iso out.zar`).
- Inside `zar zstd`, `-c` means `--compress` whether it appears before or
  after the `zstd` token.
- `zar -o game.zar` (no input) and `--iso` + `--batch` fail loud instead of
  running the wrong operation; a batch whose only failures are collision
  refusals reports the documented `-11`, a missing 7z binary or unreadable
  batch input directory reports `-13`.
- Same-stem batch archives get separate scratch and extraction destinations,
  so parallel items no longer race.

### Correctness fixes
- **CP1252 name identity:** the writer keys and sorts names by their
  on-disk Windows-1252 bytes, so names that differ only in characters CP1252
  cannot represent become one node exactly like the C++ tool (truncation
  happens before encoding, matching `substr(0, 0x7FFF)`).
- **Splitter rep history:** the repeat-offset snapshot is taken before
  `resolveOffCodes`, matching native's `dRepOriginal` capture for raw/RLE
  partitions.
- **Stream parsing:** frame headers are rebased after staging-buffer
  compaction (a header at the buffer tail no longer misreads), skippable
  payloads are not staged, and the decoded frame buffer is released as soon
  as its tail is served.
- **Seekable ranges:** `SeekableReader.DecompressRange`/`DecompressFrames`
  range errors are now `ArgumentOutOfRangeException` (and >2 GiB ranges are
  rejected before allocating); the `zar seekable` CLI maps them to the clean
  `-12` failure instead of an unhandled crash.
- **Reader robustness:** wrap-safe offset/child-range checks in
  `OffsetInfo.IsWithinValidRange`, `ReadDirectory`/`LookUp`, and
  `ReadFromFile` returning a short read (not EOF) on a mid-file block fault.
- **Process runner:** stderr is drained after exit for late failure lines,
  and a child that closed stdout but keeps running is killed on cancellation.
- **Delete-source ordering:** batch sources are removed only after the
  terminal `.zar` stage actually completed.

## Upgrade notes

- **Bytes are unchanged for ordinary inputs.** Level-6 output stays
  byte-identical to v1.1.0 and libzstd 1.5.7, and names representable in
  Windows-1252 are unaffected. Two edge cases now match the native reference
  more closely: names containing characters CP1252 cannot represent collapse
  to one node (as in C++) instead of producing a distinct node that could
  overwrite on extract, and high-level block-splitter repeat history matches
  native `dRepOriginal` capture (output can differ from v1.1.0 only where
  v1.1.0 differed from libzstd).
- **`SeekableReader` range errors are now `ArgumentOutOfRangeException`**
  (was `ZstdException`). Code catching `ZstdException` around
  `DecompressRange`/`DecompressFrames` must also catch
  `ArgumentOutOfRangeException`.
- **`ZArchiveReader.TryOpen(Stream, leaveOpen: false)` disposes the stream
  when the open fails.** Ownership is only transferred on success.
- **`ZarPackEngine.PackEntries` returns the path actually written** (an
  `AutoRename`/`Overwrite` race can re-resolve it) and takes an optional
  `ZarCollisionPolicy`; `ZarPackEngine.MoveIntoPlace` and
  `ZarPackEngine.OutputExistsMessage` are new. Existing calls remain
  source-compatible.
- **`PauseTokenSource` is now `IDisposable`**; dispose it after the workers
  stop (waiting on a disposed source throws).
- **Telemetry is opt-out.** Pass `--no-telemetry` (or set
  `ZAR_BUG_REPORT=off`) to disable usage stats, the update check, and bug
  reports.
- **Decoder total-output cap:** `ZstdDecoderOptions.MaxTotalOutputSize`
  defaults to 1 GiB for concatenated frames; set it to `ulong.MaxValue` for
  the old unbounded behavior.

## Full change list since v1.1.0

- `feat:` CLI logging through Serilog with bug-report forwarding and launch
  usage tracking
- `feat:` CLI update check, obfuscated API key, report redaction, and
  double-click console hold
- `fix:` extraction hardening (zip-slip, depth cap, scratch-and-move),
  bounded recursion, frame-buffer release, and batch isolation
- `fix:` delete-source ordering, collision-race resolution, process-runner
  stderr drain and cancellation kill, and frame caps
- `fix:` batch scratch races, CLI option validation, exit codes, and cleanup
  (including `--` inside subcommands, dashed values, and deferred `-c`)
- `fix:` exit codes, overwrite ordering, worker-pool races, and seekable
  frame caps
- `fix:` splitter repeat history, stream parsing, and CLI `-c`/`-o` handling
- `fix:` logging guards, CP1252 name identity, argument contracts, and
  policy gaps (`SeekableReader`/`ZstdDecompressor` validation)
- `fix:` telemetry exit stall, seekable `--to` crash, update-prompt hang,
  and bug-report username redaction (post-review)
- `refactor:` drop the unused zstd level parameter, clarify ultra first-block
  length
- `test:` CLI regression tests for option validation, telemetry latency,
  extraction safety, and seekable/writer contracts
- `chore:` Meziantou.Analyzer `3.0.253` → `3.0.254`, Serilog `4.4.0`,
  formatter/style pass
