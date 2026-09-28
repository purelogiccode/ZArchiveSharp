# What's New in v1.4.0

**Versioning:** package versions derive from git tags via MinVer
(`v`-prefixed annotated tags). Tagging `v1.4.0` stamps 1.4.0 on the
`ZArchiveSharp` library, the `ZArchiveSharp.Cli` (`zar`) tool, the test
projects and the benchmarks together.

**Provenance:** Release build, `0` warnings / `0` errors;
`4292/4292` library tests + `43/43` CLI battle tests green (net10.0). CI
(`.github/workflows/ci.yml`) repeats both suites on Ubuntu/Windows/macOS —
with no native toolchain installed — and packs both NuGet packages on every
push.

**License:** **MIT**, for the complete codebase. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Highlights

### Original, MIT-licensed pipeline layer
The batch pipeline was rewritten as an independent implementation:
`ZarPipeline`, `ZarPackEngine`, `ProcessRunner`, `SevenZip`,
`ProcessableFiles`, `ZarSettings`, the stage-weight/progress model and the
CLI batch orchestration. The public surface is unchanged — same types,
members, progress semantics, collision policies and exit codes — but the
repository no longer contains code derived from non-MIT sources. The frozen
reference sources (ZArchive, libzstd, zeekstd, ZstdSharp) remain
acknowledged in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) as
specification/oracle references only.

### Stricter per-item batch isolation
- **Malformed source paths fail their own batch item.** Destination
  resolution now runs inside the per-item fault guard, so a source that
  cannot be mapped to an output (invalid path characters, for example) comes
  back as a `Failed` `ZarItemResult` for that item instead of an
  `AggregateException` that aborts `ZarPipeline.PackBatch`/`ExtractBatch`.
- **`ProcessRunner` cannot stall on a held stderr pipe.** When a grandchild
  process inherits the tool's stderr and keeps it open past the tool's exit,
  the bounded drain wait now stops the asynchronous pump — keeping the lines
  already read for the failure message — instead of leaving it hanging.

### Continuous integration
`.github/workflows/ci.yml` builds and tests on Ubuntu, Windows and macOS,
packs both NuGet packages, and publishes on `v*` tags:

- full history is fetched so MinVer sees the release tags (a shallow
  checkout stamped `0.0.0-alpha` and broke the `--iso` tests);
- CLI test harnesses fold POSIX exit statuses back to the signed codes the
  assertions use and quote the resolved executable path;
- the same-stem batch tar test writes Ustar (the Homebrew 7z on macOS
  runners cannot read .NET's default PAX tar).

### Maintenance
- Package bumps: Meziantou.Analyzer 3.0.290, Microsoft.NET.Test.Sdk
  18.10.1, coverlet.collector 10.1.0, XISOSharp 1.4.0.
- IDE/analyzer cleanups; the batch settled-counter is now a small reference
  type instead of a captured mutable local.

## Upgrade notes

- **No API, wire-format or CLI changes since v1.3.0.** Archive bytes, zstd
  frames, exit codes and the reader surface are unchanged; recompiling is
  enough, and no data migration is needed.
- **NuGet metadata:** packages now declare the SPDX expression
  `<license type="expression">MIT</license>` instead of embedding the
  license as `PackageLicenseFile`. `LICENSE` and `THIRD-PARTY-NOTICES.md`
  are still packed into every package.
- **Behavior changes only for malformed input:** a batch item whose output
  path cannot be resolved now reports `Failed` for that item instead of
  throwing, and a tool whose stderr stays open past exit no longer delays
  the result.

## Full change list since v1.3.0

- `license:` relicense under MIT and rewrite the pipeline layer as an
  original implementation; scrub third-party derivations from code, docs,
  packaging and git history
- `fix:` batch destination-resolution faults are isolated per item
- `fix:` stop the stderr drain pump when a grandchild holds the pipe open
- `ci:` GitHub Actions matrix (Ubuntu/Windows/macOS), NuGet packing and
  tag-driven publishing; full-history checkout for MinVer; portable CLI test
  harnesses
- `test:` Ustar same-stem batch tar; malformed-path batch isolation test
- `chore:` analyzer, test SDK, coverlet and XISOSharp package bumps
- `refactor:` captured batch settled-counter replaced by a counter object;
  IDE style cleanups
- `docs:` README, FAQ, pipeline and release-notes updates for the MIT
  relicense
