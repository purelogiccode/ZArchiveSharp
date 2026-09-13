# What's New in v1.2.2

**Versioning:** package versions derive from git tags via MinVer
(`v`-prefixed annotated tags). Tagging `v1.2.2` stamps 1.2.2 on the
`ZArchiveSharp` library, the `ZArchiveSharp.Cli` (`zar`) tool, the test
projects and the benchmarks together — MinVer is now referenced once for the
whole solution.

**Provenance:** Release build, `0` warnings / `0` errors;
`4279/4279` library tests + `43/43` CLI battle tests green
(Ubuntu/Windows/macOS via `.github/workflows/ci.yml`).

**License (since v1.2.1):** the batch pipeline layer is a port of
v1.2.0 packages were published under MIT. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Highlights

### CLI help names the executable you launched
The usage, help, and error text no longer hardcode the global-tool name
`zar`. The CLI resolves the name it was actually started as and uses it
everywhere:

- Standalone bundles: `Usage: ZArchiveSharp ...`, `ZArchiveSharp 1.2.2`
- Global tool: still `Usage: zar ...`, `zar 1.2.2`
- `dotnet ZArchiveSharp.Cli.dll`: falls back to `zar` (the documented tool name)

The substitution is token-aware, so the `.zar` file extension and `zstd`
are never touched. It covers the main help, `zstd`/`seekable` subcommand help
and parse errors, the `--stdout`/`--dict` usage errors, the missing-input
error, and `--version`.

### Windows app icon and product metadata
The CLI project now carries `ApplicationIcon` (and `Company`) metadata, so the
Windows executables — the build apphost, the published bundle, and the
`.exe` inside every release zip — embed the ZArchiveSharp icon and show
`PureLogicCode.com` in their file properties. Unix bundles are plain
executables and have no icon concept.

### Tag-driven versioning for every project
MinVer moved from the two shipped projects into `Directory.Build.props`, so
the library, CLI, tests, battle tests and benchmarks all stamp the same
release version from the git tag (`1.2.2`), with FileVersion
`1.2.2.0`/InformationalVersion `1.2.2+<commit>`.

### Standalone release bundles
Per platform/architecture zips are produced from the tagged source:

| Bundle | Executable |
|--------|------------|
| `release_1.2.2_win-x64.zip` / `win-arm64` | `ZArchiveSharp.exe` |
| `release_1.2.2_linux-x64.zip` / `linux-arm64` | `ZArchiveSharp` (0755) |
| `release_1.2.2_osx-x64.zip` / `osx-arm64` | `ZArchiveSharp` (0755) |

- **Framework-dependent:** the .NET runtime is *not* embedded; the matching
  .NET 10 runtime must be installed. Each zip carries the single-file
  executable plus its required `ZArchiveSharp.Cli.runtimeconfig.json`
  sidecar (the host reads it before loading the bundle).
- Each zip also contains `README.md`, `LICENSE` and
  `THIRD-PARTY-NOTICES.md`.
- Unix executables are marked executable in the zip metadata, so they run
  straight after unzipping.

## Upgrade notes

- **No wire-format or API changes** since v1.2.0: archive bytes, frame bytes
  and exit codes are unchanged.
- **Standalone bundles require .NET 10** (they are framework-dependent) and
  the runtimeconfig sidecar must stay next to the executable.
- **License:** v1.2.0 NuGet packages are MIT; v1.2.1 and later are
  term, so no code changes are needed.

## Full change list since v1.2.0

  `THIRD-PARTY-NOTICES.md`, switch packages to `<license type="file">`
- `feat:` CLI help/usage/version name the executable they were launched as
  (`ZArchiveSharp` vs `zar`), token-aware so `.zar` stays intact
- `chore:` Windows application icon and product metadata
- `chore:` MinVer for the whole solution so every project stamps 1.2.2
- `test:` executable-name-aware help/parity assertions
- `docs:` standalone-bundle instructions, runtime requirement, v1.2.1/v1.2.2
  release notes
