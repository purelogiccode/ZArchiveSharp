# CLI Reference

The `zar` command-line tool provides pack, extract, convert, and batch operations. It matches the `zarchive.exe` exit codes and behavior for compatibility.

## Installation

```bash
dotnet tool install -g ZARSharp.Cli
```

## Usage

```
zar [options] [input] [output]
```

## Commands

### Pack a Directory

```bash
zar <directory> [output.zar]
```

Packs the specified directory into a `.zar` archive. If no output path is specified, creates `<directory_name>.zar` in the same location.

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
| `--no-compress` | | Store blocks without compression | false |

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
