# Pipeline

`ZARSharp.Pipeline` is the shared engine behind `ZArchiveTool`, the XISO `.zar` bridges and the CLI: directory pack, archive extract, and parallel batches with progress, pause, cancellation and collision handling. One engine, one set of semantics.

## Basic Usage

### Pack

```csharp
using ZARSharp.Pipeline;

// Minimal: defaults are zstd level 6, Fail policy, 4 workers
string? result = ZarPipeline.Pack(@"C:\mydata");

// Returns the archive path written (after collision resolution),
// or null when Skip policy skipped an existing output.
string? path = ZarPipeline.Pack(
    sourceDirectory: @"C:\mydata",
    zarPath: @"C:\mydata.zar",           // null => <stem>.zar next to input
    options: new ZarPipelineOptions { Level = 9 },
    progress: new Progress<ZarProgress>(p => Console.WriteLine($"{p.Ratio:P1}")));
```

### Extract

```csharp
using ZARSharp.Pipeline;

// Returns extracted file paths relative to the archive root ('/' separated)
IReadOnlyList<string> files = ZarPipeline.Extract(
    zarPath: @"C:\mydata.zar",
    destDir: @"C:\mydata_out",
    progress: new Progress<ZarProgress>(p => Console.WriteLine($"{p.Ratio:P1}")));
```

### Custom Sources

Anything that can enumerate (path, size, content stream) pairs can be packed via `IZarPackSource` — the directory tree and the XISO walk are both built on it:

```csharp
using ZARSharp.Pipeline;

ZarPipeline.PackSource(mySource, @"C:\out.zar", options);
```

## Options


| Property | Type | Default | Meaning |
|----------|------|---------|---------|
| `Level` | `int` | 6 | zstd level 1–22 for packing (ignored for extract) |
| `Checksum` | `bool` | `false` | Per-block content checksums |
| `Compressor` | `IZarBlockCompressor?` | `null` | Explicit compressor; overrides `Level`/`Checksum` |
| `DeterministicOrder` | `bool` | `true` | Sort entries ordinally for reproducible archives |
| `CollisionPolicy` | `ZarCollisionPolicy` | `Fail` | Output-exists behavior |
| `MaxDegreeOfParallelism` | `int` | 4 | Batch workers (clamped ≥ 1; effective = `min(workers, items)`) |
| `DeleteSourceOnSuccess` | `bool` | `false` | Delete pack source after success (off by default — a library must not destroy inputs unless asked) |
| `Pause` | `PauseToken` | default | Pause gate checked alongside the cancellation token |
| `NameOrder` | `IReadOnlyList<string>?` | `null` | Pre-seeded name-table order; `null` = pack order. Pass a source-walk (discovery) order for byte-parity with packers that write names in discovery order |

```csharp
var options = new ZarPipelineOptions
{
    Level = 19,
    CollisionPolicy = ZarCollisionPolicy.AutoRename,
    MaxDegreeOfParallelism = 8,
    DeterministicOrder = true,
};
```

## Progress

Progress is reported via `IProgress<ZarProgress>`:

```csharp
var progress = new Progress<ZarProgress>(p =>
{
    Console.WriteLine($"{p.Operation} {p.CurrentFile}: {p.FilesCompleted}/{p.FilesTotal} {p.Ratio:P1}");
});
```

`ZarProgress` fields:

| Field | Meaning |
|-------|---------|
| `Operation` | `Pack` or `Extract` |
| `SourcePath` | Item being processed |
| `DestinationPath` | Output path |
| `CurrentFile` | File within the item (empty between files) |
| `FilesCompleted` / `FilesTotal` | Pre-scanned totals |
| `BytesCompleted` / `BytesTotal` | Byte progress when known |
| `Ratio` | 0–1 fraction (bytes when known, else files) |


## Cancellation and Pause

```csharp
using ZARSharp.Pipeline;

using var cts = new CancellationTokenSource();
using var pauseSource = new PauseTokenSource();

var options = new ZarPipelineOptions { Pause = pauseSource.Token };

var task = Task.Run(() =>
    ZarPipeline.Pack(@"C:\bigdata", @"C:\big.zar", options, null, cts.Token));

// Pause mid-run (checked at the same points as cancellation)
pauseSource.Pause();
await Task.Delay(5000);
pauseSource.Resume();

// Or cancel entirely — in-flight work stops; incomplete pack outputs are deleted
cts.Cancel();
```

Cancellation deletes incomplete pack outputs (the `zarchive.exe` delete-incomplete-output contract).

## Collision Policies

What happens when an output path already exists:

| Policy | Behavior |
|--------|----------|
| `Fail` | Throw `IOException` (default; preserves the `zarchive.exe` refuse-overwrite contract) |
| `Skip` | Skip the item — reported as `ZarItemStatus.Skipped` |
| `Overwrite` | Delete the existing output, then write |
| `AutoRename` | Write to `{stem}_{n}{suffix}`, first free `n` from 1 |


## Batch Operations

### Pack Batch

```csharp
using ZARSharp.Pipeline;

var results = ZarPipeline.PackBatch(
    sourceDirectories: [@"C:\game1", @"C:\game2", @"C:\game3"],
    destDir: @"C:\archives",             // null => each .zar next to its source
    options: new ZarPipelineOptions { MaxDegreeOfParallelism = 4 },
    progress: progress);

foreach (var r in results)
{
    Console.WriteLine($"{r.SourcePath}: {r.Status} {r.ErrorMessage}");
}
```


- Worker count is `min(MaxDegreeOfParallelism, items)` — never spins up more tasks than items
- **One item's failure does not stop the others**; per-item outcomes come back as `ZarItemResult`
- Batch progress re-bases per-item ratios into `1/n` shares

### Extract Batch

```csharp
var results = ZarPipeline.ExtractBatch(
    zarPaths: [@"C:\a.zar", @"C:\b.zar"],
    destRoot: @"C:\extracted",
    options: options);
```

### Batch Requests

For UIs, a `ZarBatchRequest` models the full input set with modes and collision handling; `ZarItemResult`/`ZarItemStatus` carry outcomes (`Completed`, `Skipped`, `Failed`, ...).

## Exit Codes (zarchive.exe Contract)

`ZarchiveCli.Run` is the callable form of the `zarchive.exe input_path [output_path]` contract: directory input packs, file input extracts, outputs default to `<stem>.zar` / `<stem>_extracted`, existing pack outputs are refused, incomplete outputs are deleted.

```csharp
using ZARSharp.Pipeline;

int code = ZarchiveCli.Run(
    args: ["C:\\mydata", "C:\\mydata.zar"],
    options: options,
    log: Console.WriteLine);   // receives the native stdout chatter
```

Exit codes (same negative values `main()` returns; `-2`, `-5`–`-9` unused upstream too):

| Code | Constant | Meaning |
|------|----------|---------|
| `0` | `Ok` | Success |
| `-1` | `BadUsage` | Too many paths; input neither file nor directory |
| `-3` | `OutputNotDirectory` | Extract output exists and is not a directory |
| `-4` | `OutputDirectoryNotCreated` | Could not create output directory |
| `-10` | `NotFound` | Archive not found; pack output not a regular file |
| `-11` | `Refused` | Archive failed to open; pack output already exists |
| `-12` | `ExtractionFailed` | Corrupt archive or extract I/O |
| `-13` | `PackFailed` | Archive structure error |
| `-14` | `ArchiveEntryFailed` | Duplicate or bad path |
| `-15` | `InputNotReadable` | Could not open an input file |
| `-16` | `PackOutputFailed` | Output I/O error |

Automation matching on these codes keeps working unchanged.

### Stdout Chatter

With `log` supplied, `ZarchiveCli` reproduces native chatter:

- Pack: `Outputting to: ...` then `Adding <path>` per file (OS separators in the display line)
- Extract: `Extracting to: ...` then per-entry lines with the native **leading-`/` quirk**, directories included, preorder — even on mid-archive failure

### Three Intentional Deviations

Where native behavior is a bug, ZARSharp deviates (all tested):

1. An unopenable extract output **throws** (native prints `Unable to write file:` then keeps writing into the dead stream)
2. A mid-file input read error **fails the pack with `-16`** (native treats a short read as EOF and silently packs a truncated file)
3. Error-string paths use `/` on every OS (native `pathEntry.string()` prints `\` on Windows; only the `Adding` display line converts)

## Exception Types

| Exception | When |
|-----------|------|
| `ZarArchiveOpenException` | Archive failed to open/validate (maps to `-11`) |
| `ZarInputOpenException` | Pack could not open an input file (maps to `-15`) |
| `ZarEntryCreateException` | Duplicate or bad archive path (maps to `-14`) |
| `IOException` | Output I/O errors (maps to `-16`); also `Fail`-policy collisions |
| `InvalidOperationException` | Archive structure errors (maps to `-13`) |
| `OperationCanceledException` | Cancelled — propagated, never swallowed |

## Config and File Discovery


## ProcessRunner

