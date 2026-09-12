# ZArchiveSharp.Cli

Command-line tool for creating and extracting ZArchive (`.zar`) files using [ZArchiveSharp](https://www.nuget.org/packages/ZArchiveSharp) — pack a directory, extract an archive, convert Xbox ISOs (XISO), or batch-process a folder in parallel.

A pure-C# port of the `zarchive.exe` contract (ZArchive 0.1.2): same arguments, defaults, stdout chatter and exit codes, with three documented deviations where native behavior is a bug.

## Install

```bash
dotnet tool install -g ZArchiveSharp.Cli
```

## Usage

```
zar [options] [input] [output]
```

| Command | Description |
|---------|-------------|
| `zar <directory> [output.zar]` | Pack a directory to `.zar` |
| `zar <archive.zar> [output_dir]` | Extract `.zar` to a directory |
| `zar --iso <game.iso> [output.zar]` | Convert XISO to `.zar` (Redump-aware; output positionally or via `-o`, not both) |
| `zar zstd <in> [out]` | Raw zstd compress/decompress (`--dict`, `--stdout`, `--check`) |
| `zar seekable compress [in] [out]` | Seekable-zstd compress (`-l`, `-s`, `--checksum`, `--seek-table-file`) |
| `zar seekable decompress [in] [out]` | Seekable-zstd decompress (`--from/--to`, `--from-frame/--to-frame`) |
| `zar seekable list <file>` | Frame/seek-table detail tables |

Outputs default to `<stem>.zar` / `<stem>_extracted` next to the input, like `zarchive.exe`.
Args are exactly `input [output]` — extras fail with `-1` / `Too many paths specified`, never silently dropped. `-o/--output` occupies the output slot, so `zar in -o out extra` is a usage error.

Batch mode (`-b/--batch`) processes every file under the input directory:
archives (`.zip/.rar/.7z/.tar/.gz` via an installed 7z, `--seven-zip` override)
→ ISO/dir → `.zar`; `--mode auto/extract-archive/extract-iso/compress`
selects the legs, `--keep-originals`/`--delete-source` (default keep) controls
cleanup, and `--policy` (fail/skip/overwrite/auto-rename) applies to batch
collisions only.

## Options

```
  -l, --level <N>       Compression level 1-22 (default: 6)
  -j, --jobs <N>        Parallel workers: batch items plus 64 KiB block fan-out inside a single pack/extract, capped by CPU, byte-identical (default: 4)
  -p, --policy <P>      Collision policy: fail, skip, overwrite, auto-rename
  -b, --batch           Batch process all files in input directory
  -o, --output <path>   Output path
  -q, --quiet           Suppress output
      --no-compress     Store blocks without compression
  -v, --version         Show version
  -h, --help            Show this help
```

## Examples

```bash
# Pack (zstd level 6 by default, deterministic order)
zar C:\game C:\game.zar

# Maximum compression
zar -l 19 C:\game C:\game.zar

# Extract
zar C:\game.zar C:\game_out

# Convert an Xbox ISO
zar --iso C:\game.iso C:\game.zar

# Batch-pack every processable file under C:\games with 8 workers
zar -b -j 8 C:\games C:\archives
```

## Exit Codes

Identical to `zarchive.exe` (`-2` and `-5`..`-9` are unused upstream too):

| Code | Meaning |
|------|---------|
| `0` | Success |
| `-1` | Usage error (too many paths; input neither file nor directory) |
| `-3` | Extract output path exists and is not a directory |
| `-4` | Extract output directory could not be created |
| `-10` | Archive not found; pack output exists and is not a regular file |
| `-11` | Archive failed to open; pack output already exists |
| `-12` | Extraction failed (corrupt archive or I/O) |
| `-13` | Pack failed on archive structure |
| `-14` | Pack failed to create an archive entry (duplicate or bad path) |
| `-15` | Pack failed to open an input file |
| `-16` | Pack failed on output I/O |

## Format Notes

- `.zar` is the ZArchive 0.1.2 format: 64 KiB blocks, each independently zstd-compressed (raw fallback when compression does not shrink), big-endian structures, Windows-1252 names, footer SHA-256
- Compression is byte-identical to libzstd 1.5.7; archives interop with `zarchive.exe` both ways
- No native dependencies, no `unsafe`, trimmable and AOT-compatible

## Documentation

Full documentation lives in the [repository wiki](https://github.com/purelogiccode/ZArchiveSharp/wiki) ([docs/](https://github.com/purelogiccode/ZArchiveSharp/tree/master/docs)): format spec, compression guide, pipeline API, benchmarks and FAQ.

## License

MIT — see [LICENSE](../LICENSE).
