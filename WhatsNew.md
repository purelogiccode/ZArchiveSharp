# What's New in v1.1.0

**Versioning:** package versions derive from git tags via MinVer
(`v`-prefixed annotated tags). Tagging `v1.1.0` stamps 1.1.0 on the
`ZArchiveSharp` library and the `ZArchiveSharp.Cli` (`zar`) tool together.

**Provenance:** Release build, `0` warnings / `0` errors;
`4190/4190` library tests + `40/40` CLI battle tests green
(Ubuntu/Windows/macOS via `.github/workflows/ci.yml`).

## Highlights

### Block-level parallelism (single pack/extract)
`ZarPipelineOptions.MaxDegreeOfParallelism` (default `4`, capped by CPU
count) now also fans out the 64 KiB block compression/decompression
*inside* a single pack/extract — previously it only parallelized batches.
Each block is an independent zstd frame, so parallel output is
**byte-identical** to sequential output (locked by tests across levels
1–22, dictionaries, and checksums).

- `ZArchiveWriter` takes an optional per-worker `compressorFactory`;
  explicit `IZarBlockCompressor` instances stay on the sequential path
  (foreign thread-safety is unknown — raw storage is a memcpy anyway).
- Extract decodes bounded parallel waves and writes them in order, with
  the same corruption contract as sequential
  (`InvalidOperationException("Extraction failed: ...")`,
  `OperationCanceledException` propagates raw).

### Codec scratch pooling
The zstd match-finder tables, frame copies, and sequence stores are now
rented from shared pools instead of allocated per block — same bytes,
less GC pressure on large packs.

### Hardened contracts (bug fixes since v1.0.2)
- Parallel-pack emit faults can no longer double-return pooled buffers.
- Parallel-extract decoder faults surface unwrapped (no leaked
  `AggregateException`).
- The pooled frame copy exposes only its logical length, so pooled tail
  bytes can never leak into output.
- Ultra2 seeding sizes its throwaway store by block length, not absolute
  offset (no over-allocation with dictionaries / multi-block frames).

### CLI: positional contract matches the oracle
`zar` args are exactly `input [output]`, like `zarchive.exe`:
a third positional fails with `-1` / `Too many paths specified`
(previously silently dropped). The rule extends to the zar-only flags:

- `-o/--output` occupies the output slot — `zar in -o out extra`
  is a usage error, never a silent drop.
- `zar --iso x [out.zar]` accepts the output positionally or via `-o`
  (not both); stray positionals are rejected.

### CLI battle-test suite
New `ZArchiveSharp.CliBattleTests` project shells out to the reference
`zarchive.exe` oracle and locks exit codes, stdout, and pack/extract
cross-interop fixture by fixture. It skips cleanly when the oracle is
absent (clean clones / CI without `References/zarchive.exe`).

### Packaging
- The CLI consumes XISOSharp as a NuGet package (`1.0.2`) — no sibling
  checkout needed; `-p:XisoSharpAvailable=false` still builds the
  no-XISO shape.
- Single-file publish emits the bundle as `ZArchiveSharp.exe`.

## Upgrade notes

- **Drop-in compatible:** archive bytes, frame bytes, exit codes, and
  the default level-6 output are unchanged. No API breaks.
- **More threads by default:** a default `ZarPipelineOptions` pack/extract
  now uses up to 4 block workers (capped by processor count) instead of
  1 thread. Pass `MaxDegreeOfParallelism = 1` for the old single-threaded
  profile. Direct `ZArchiveWriter` use still defaults to sequential
  (`maxDegreeOfParallelism: 1`).
- **Batch + blocks multiply:** `PackBatch` outer workers × inner block
  workers can reach ~16 codec threads at DOP 4 on many-core machines;
  lower one of them if that oversubscribes your host.

## Full change list since v1.0.2

- `feat:` parallel block compression/decompression plus codec scratch pooling
- `fix:` reject more than two positional paths like `zarchive.exe`
- `fix:` harden parallel pack/extract pooling, frame-length, error
  contracts and CLI positional handling (incl. `-o`/`--iso` guards,
  worker growth, sequence-store pool retention)
- `fix:` remove dead `NewTables`, resolve doc reference
- `test:` CLI battle-test project + parallel regression tests
  (multi-wave, determinism, second-wave corruption, fault injection,
  uncovered levels)
- `chore:` XISOSharp as NuGet, single-file bundle rename,
  Meziantou.Analyzer `3.0.250` → `3.0.253`, formatter cleanup
