# CLI Reference

The `zar` command-line tool provides pack, extract, convert, and batch operations. It matches the `zarchive.exe` exit codes and behavior for compatibility.

## Installation

```bash
dotnet tool install -g ZArchiveSharp.Cli
```

## Usage

```
zar [options] [input] [output]
zar zstd -c|-d [options] [input] [output]
zar seekable compress|decompress|list [options] [input] [output]
```

Args are exactly `input [output]`, like `zarchive.exe`: extras fail
with `-1` / `Too many paths specified`, never silently dropped.
`-o/--output` occupies the output slot (`zar in -o out extra` is a usage
error); `zar --iso x [out.zar]` takes the output positionally or via
`-o`, not both.

## Commands

### Compress/Decompress Single zstd Streams

```bash
zar zstd -c [input] [output]
zar zstd -d [input] [output]
```

Compresses or decompresses a single zstd stream (not a `.zar` archive).
Omitted input reads stdin, omitted output writes stdout, so pipes work:

```bash
zar zstd -c big.bin | zar zstd -d > big.back
zar zstd -c big.bin compressed.zst
zar zstd -d compressed.zst restored.bin
```

With `--dict`, both sides use the dictionary (zstd `-D` semantics — the
dictionary file is never stored, keep it alongside):

```bash
zar zstd -c --dict words.dict small.txt small.zst
zar zstd -d --dict words.dict small.zst restored.txt
```

Subcommand options: `-c/--compress`, `-d/--decompress` (exactly one is
required), `-l/--level <N>` (compress only), `--dict <file>`,
`--check/--no-check` (compress only, last wins), `--stdout` (explicit
stdout; an error together with an output path), `-q/--quiet`, `-h/--help`.
Inside `zar zstd`, `-c` means `--compress` (not `--stdout`).

Note: a pack/extract path literally named `zstd` must be spelled `./zstd`
so it is not taken for the subcommand.

### Seekable zstd Files

```bash
zar seekable compress [input] [output]
zar seekable decompress [input] [output]
zar seekable list <file>
```

Compresses, decompresses, or inspects seekable zstd files (zeekstd-compatible
framing: independently compressed frames plus a seek table) — not single-frame
`zar zstd` streams and not `.zar` archives. Omitted input reads stdin
(compress streams it in 128 KiB chunks; decompress buffers it, since the Foot
table lives at the end of the file), omitted output writes stdout, so pipes work:

```bash
zar seekable compress big.bin | zar seekable decompress > big.back
zar seekable compress big.bin big.zst
zar seekable decompress big.zst restored.bin
zar seekable list big.zst
```

Compress derives the output name when omitted (`<input>.zst`); decompress
defaults to stdout. Compress options: `-l/--level <N>` 1–22 (default: 3, like
the oracle — an explicit global `-l` before `seekable` overrides it),
`-s/--frame-size <S>` with `B/K/M/G` suffixes (default: `2M`, capped at `1G`),
`--frame-size-policy compressed|uncompressed` (default: uncompressed),
`--checksum/--no-checksum` (also `--check/--no-check`; default on, last wins),
`--seek-table-file <file>` (write a standalone Head table there instead of
appending the Foot table). Decompress options: `--from/--to` byte offsets
(`--to` takes `end`), `--from-frame/--to-frame` frame indices (`--to-frame`
takes `last`), `--seek-table-file <file>` (read the table from there instead
of the file tail). List options: `--from-frame/--to-frame/--num-frames`,
`-d/--detail` (per-frame table; implied by frame bounds),
`--seek-table-format foot|head` (`head` means the input IS a standalone table
file). `-f/--force` overwrites existing outputs (default: refuse, `-11`);
`-c/--stdout` forces stdout; `-q/--quiet` suppresses the input → output line
(ignored by `list`, whose table always prints). A global `--dict` with
`seekable` is a usage error (seekable frames carry no dictionary).

Subcommand aliases: `c`, `d`, `l`. A path literally named `seekable` must be
spelled `./seekable`.

Deliberate deviations from the `zeekstd` 0.4.5 CLI: argument shape follows this
CLI's convention (positional `[input] [output]`, not `-o`); existing outputs
are refused rather than prompting (`-f` overrides); sizes always print raw
(no humanized units, no `-r/--raw-bytes`); no progress bar (no
`--no-progress`); `--patch-from/--patch-apply` (diff engine) and
`--mmap-prefix` are unsupported (managed code, no diff engine); levels run
1–22 (the oracle stops at 19); size suffixes accept any case.

### Pack a Directory

```bash
zar <directory> [output.zar]
```

Packs the specified directory into a `.zar` archive. If no output path is specified, creates `<directory_name>.zar` in the same location. `--dict` packs with dictionary frames (extract needs the same `--dict`); `--no-compress` stores raw and ignores `--dict`.

**Examples:**

```bash
# Pack with default settings
zar C:\game

# Pack to specific output
zar C:\game C:\archives\game.zar

# Pack with custom compression level
zar -l 9 C:\game C:\game.zar
```

### Extract an Archive

```bash
zar <archive.zar> [output_dir]
```

Extracts the archive to the specified directory. If no output path is specified, creates `<archive_name>_extracted` in the same location.

**Examples:**

```bash
# Extract with default output
zar C:\game.zar

# Extract to specific directory
zar C:\game.zar C:\extracted\game
```

### Convert XISO to ZAR

```bash
zar --iso <game.iso> [output.zar]
```

Converts an Xbox ISO (XISO) file to a `.zar` archive. Requires the XISOSharp dependency.

The CLI consumes XISOSharp as a NuGet package, so `zar --iso` works out of a
clean clone. Building with `-p:XisoSharpAvailable=false` produces a tool
without XISO support: `zar --iso` fails fast on stderr (exit `-1`) with
rebuild instructions and `--help` marks the flag unavailable; everything else
works.

Redump ISOs (full disc dumps starting with the video partition) are
auto-detected by exact file size: the CLI resolves the wave-dependent game
partition offset from the same `XgdTables` the `XISOSharp.Cli --zar` mode
uses (PVD wave read for the ambiguous sizes, video-type-0 fallback when the
wave is unreadable) and packs the game partition — never the video area. A
Redump pack is byte-identical to packing the plain extracted XISO. `game`
derives from `game.redump.iso` to `game.zar`.

**Examples:**

```bash
# Convert with default output
zar --iso C:\game.iso

# Convert to specific output
zar --iso C:\game.iso C:\game.zar
```

### Batch Operations

```bash
zar --batch <input_dir> [output_dir]
```

Processes all eligible files in the input directory in parallel.

- `auto` (default): archives run the 7z container stage (extract to
  `temp_<stem>`, first `.iso` keeps going as `<stem>.iso`, otherwise the
  whole tree becomes `<stem>/`) then continue to `.zar`; plain ISOs
  convert straight to `.zar`; directories pack to `.zar`.
- `extract-archive` (aliases `extract-arc`, `archive`): `.zip/.rar/.7z/.tar/.gz`
  only — extract with 7z and stop (no `.zar`).
- `extract-iso` (aliases `extract`, `iso`): `.iso` only — convert to `.zar`.
- `compress`: directories only — pack to `.zar`.

7z stays external: the CLI uses `7z`/`7zz` from `PATH` (plus the standard
Windows install location); `--seven-zip <exe>` overrides the path, and
archive items fail with rebuild-free instructions when no binary is found.
`--delete-source` removes each source (archive *and* intermediate) after
sources are kept by default (`--keep-originals`, last wins against
`--delete-source`). The collision `--policy` applies to every stage output
(intermediate and `.zar`). All three flags are `--batch`-only.

**Examples:**

```bash
# Batch process with defaults
zar --batch C:\games

# Batch process with 8 workers
zar -b -j 8 C:\games C:\archives

# Pack only the extracted directories, deleting each source on success
zar -b --mode compress --delete-source C:\games C:\archives

# Extract just the containers, keep the trees/ISOs, no .zar files
zar -b --mode extract-archive C:\games C:\unpacked

# Use a 7z outside PATH
zar -b --seven-zip "D:\tools\7z.exe" C:\games C:\archives
```

---

## Options

### Compression

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--level <N>` | `-l` | Compression level (1–22; `seekable compress`: 1–22, default 3) | 6 |
| `--dict <file>` | | Dictionary file (pack/`zstd`; never stored, keep alongside) | none |
| `--check` / `--no-check` | | Write / omit content checksums (pack/`zstd` compress; last wins; `seekable compress` defaults on) | off |
| `--no-compress` | | Store blocks without compression (ignores `--dict`) | false |

### Input / Output

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--stdout` | `-c` | Stream to stdout — only with `zar zstd` (its default output); inside `zar zstd`, `-c` means `--compress` | off |

### Output

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--output <path>` | `-o` | Output path | Auto-derived |
| `--policy <P>` | `-p` | Collision policy: `fail`, `skip`, `overwrite`, `auto-rename` (`--batch` only) | `fail` |

### Parallelism

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--jobs <N>` | `-j` | Parallel workers: batch items plus 64 KiB block fan-out inside a single pack/extract (capped by CPU; byte-identical) | 4 |
| `--mode <M>` | | Batch stages: `auto`, `extract-archive`, `extract-iso`, `compress` (`--batch` only) | `auto` |
| `--seven-zip <exe>` | | 7z binary for the archive stage: explicit path, else `PATH` + install location (`--batch` only) | auto-detect |
| `--keep-originals` / `--delete-source` | | Keep / delete each batch source after its pack succeeds (`--batch` only, last wins) | keep |

### Other

| Option | Short | Description |
|--------|-------|-------------|
| `--quiet` | `-q` | Suppress output |
| `--version` | `-v` | Show version |
| `--help` | `-h` | Show help |

---

## Exit Codes

The CLI returns the same exit codes as `zarchive.exe` for compatibility:

| Code | Constant | Description |
|------|----------|-------------|
| `0` | `Ok` | Success |
| `-1` | `BadUsage` | Usage error (too many paths, invalid input) |
| `-3` | `OutputNotDirectory` | Extract output path exists and is not a directory |
| `-4` | `OutputDirectoryNotCreated` | Extract output directory could not be created |
| `-10` | `NotFound` | Archive file not found, or pack output exists and is not a regular file |
| `-11` | `Refused` | Archive failed to open, or pack output already exists |
| `-12` | `ExtractionFailed` | Extraction failed (corrupt archive or I/O error) |
| `-13` | `PackFailed` | Pack failed on archive structure |
| `-14` | `ArchiveEntryFailed` | Pack failed to create an archive entry (duplicate or bad path) |
| `-15` | `InputNotReadable` | Pack failed to open an input file |
| `-16` | `PackOutputFailed` | Pack failed on output I/O |
| `130` | — | Interrupted by Ctrl+C (shell SIGINT convention, not a pack/extract code) |

`zar zstd` reuses this table with no new codes: compress failures report
pack codes (`-13` failure, `-15` unreadable input, `-16` uncreatable
output), decompress failures report extract codes (`-12` failure, `-10`
missing input). Refusing to overwrite an existing output is `-11` on both
sides; a missing/unreadable `--dict` is `-1`. Errors go to stderr; the
input → output line goes to stdout, except to stderr when stdout carries
binary data. A file output created by `zar zstd` is deleted when the run
fails, like incomplete pack outputs.

`zar seekable` reuses the same table the same way: compress failures report
pack codes (`-13` failure, `-15` unreadable input, `-16` uncreatable output),
decompress and list failures report extract codes (`-12` failure — including
a missing/unparseable seek table, an out-of-range frame, or a bad range —
`-10` missing input/table file). Refusals are `-11`, bad ranges that do not
depend on the file (`--from` past `--to`, start frame past end frame) are
`-1`. Failed runs delete the file outputs they created.

---

## Collision Policies

On `--batch` runs, when a stage output already exists, the `--policy` option controls behavior:

| Policy | Behavior |
|--------|----------|
| `fail` | Exit with error code `-11` (default, matches `zarchive.exe`) |
| `skip` | Skip the item, continue processing |
| `overwrite` | Delete existing file and write new one |
| `auto-rename` | Write to `{stem}_{n}{suffix}` (first free `n` from 1) |

Single pack/extract/`--iso` and the `zstd`/`seekable` subcommands keep the
`zarchive.exe` contract instead: an existing pack output is refused with
`-11` (extract overwrites into the destination directory, like the native
tool), and an explicit non-`fail` `--policy` there is a `-1` usage error —
never silently ignored. Unknown values (`--policy bogus`) and a missing
value are `-1` on every path.

---

## Examples

### Basic Pack/Extract Workflow

```bash
# Pack a directory
zar C:\myproject C:\myproject.zar

# Verify the archive
zar C:\myproject.zar C:\verify
```

### High-Compression Archive

```bash
# Use level 19 for maximum compression
zar -l 19 C:\data C:\data.zar
```

### Batch Processing

```bash
# Process all directories in a folder with 8 workers
zar -b -j 8 C:\games C:\archives

# Overwrite existing archives
zar -b -p overwrite C:\games C:\archives
```

### XISO Conversion

```bash
# Convert Xbox ISO to ZAR
zar --iso C:\games\game.iso C:\games\game.zar
```

### Seekable Files

```bash
# Compress with small frames, inspect, slice-decode one range
zar seekable compress -s 256K big.bin big.zst
zar seekable list big.zst
zar seekable list --detail --from-frame 2 --to-frame 4 big.zst
zar seekable decompress --from 1M --to 2M big.zst slice.bin
```

### Quiet Mode

```bash
# Suppress all output
zar -q C:\data C:\data.zar
```

---

## Stdout Behavior

The CLI produces output matching `zarchive.exe`:

**Pack mode:**
```
Outputting to: C:\data.zar
Adding file1.txt
Adding subdir/file2.dat
```

**Extract mode:**
```
Extracting to: C:\data_extracted
/file1.txt
/subdir
/subdir/file2.dat
```

Note: Extract entry lines include a leading `/` (the native quirk) and use OS-native path separators for the `Adding` display line only.

---

## Differences from `zarchive.exe`

ZArchiveSharp's CLI is intentionally compatible but has three documented deviations where native behavior is a bug:

1. **Unopenable extract output** — Throws an exception (native writes into the dead stream)
2. **Mid-file read errors** — Fails the pack with `-16` (native silently truncates)
3. **Error string paths** — Uses `/` on every OS (native uses `\` on Windows)
