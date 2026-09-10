# CLI Reference

The `zar` command-line tool provides pack, extract, convert, and batch operations. It matches the `zarchive.exe` exit codes and behavior for compatibility.

## Installation

```bash
dotnet tool install -g ZARSharp.Cli
```

## Usage

```
zar [options] [input] [output]
zar zstd -c|-d [options] [input] [output]
```

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

**Examples:**

```bash
# Batch process with defaults
zar --batch C:\games

# Batch process with 8 workers
zar -b -j 8 C:\games C:\archives
```

---

## Options

### Compression

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--level <N>` | `-l` | Compression level (1–22) | 6 |
| `--dict <file>` | | Dictionary file (pack/`zstd`; never stored, keep alongside) | none |
| `--check` / `--no-check` | | Write / omit content checksums (pack/`zstd` compress; last wins) | off |
| `--no-compress` | | Store blocks without compression (ignores `--dict`) | false |

### Input / Output

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--stdout` | `-c` | Stream to stdout — only with `zar zstd` (its default output); inside `zar zstd`, `-c` means `--compress` | off |

### Output

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--output <path>` | `-o` | Output path | Auto-derived |
| `--policy <P>` | `-p` | Collision policy: `fail`, `skip`, `overwrite`, `auto-rename` | `fail` |

### Parallelism

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--jobs <N>` | `-j` | Number of parallel workers | 4 |

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

---

## Collision Policies

When the output file already exists, the `--policy` option controls behavior:

| Policy | Behavior |
|--------|----------|
| `fail` | Exit with error code `-11` (default, matches `zarchive.exe`) |
| `skip` | Skip the item, continue processing |
| `overwrite` | Delete existing file and write new one |
| `auto-rename` | Write to `{stem}_{n}{suffix}` (first free `n` from 1) |

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

ZARSharp's CLI is intentionally compatible but has three documented deviations where native behavior is a bug:

1. **Unopenable extract output** — Throws an exception (native writes into the dead stream)
2. **Mid-file read errors** — Fails the pack with `-16` (native silently truncates)
3. **Error string paths** — Uses `/` on every OS (native uses `\` on Windows)
