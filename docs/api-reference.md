# API Reference

Complete API documentation for ZArchiveSharp. All types are in the `ZArchiveSharp` namespace unless otherwise noted.

## Namespaces

| Namespace | Contents |
|-----------|----------|
| `ZArchiveSharp` | Archive reader/writer, format structures, tool |
| `ZArchiveSharp.Zstd` | zstd encoder/decoder, compression options |
| `ZArchiveSharp.Seekable` | Seekable zstd format (Foot + Head) |
| `ZArchiveSharp.Pipeline` | Pipeline engine, batch operations, progress |

## Changed in v1.2.0

| Area | Change |
|------|--------|
| `SeekableReader.DecompressRange` / `DecompressFrames` | Range errors now throw `ArgumentOutOfRangeException` (was `ZstdException`); ranges larger than `int.MaxValue` are rejected before allocating |
| `ZArchiveReader.TryOpen(Stream, leaveOpen: false)` | Disposes the stream when the open fails (ownership transfers only on success) |
| `ZarPackEngine.PackEntries` | Returns the path actually written and takes an optional `ZarCollisionPolicy` |
| `ZarPackEngine` | New `MoveIntoPlace`, `OutputExistsMessage`, and `MaxExtractDepth` members |
| `ZstdDecoderOptions` | New `MaxTotalOutputSize` cumulative cap (default 1 GiB) for concatenated frames |
| `PauseTokenSource` | Now implements `IDisposable`; dispose after workers stop |
| `ZstdDecompressor.Decompress` / `DecompressExact` | Validate `src`/`dst`/`options` and both ranges up front |
| Extraction | Rejects unsafe entry names (zip-slip, device names), builds through scratch files, and caps nesting at `MaxExtractDepth` |
| `DirectoryPackSource` | Never descends directory symlinks/junctions (the link stays as an empty directory entry) |

---

## ZArchiveWriter

Writes `.zar` archive files. Faithful port of `zarchivewriter.cpp`.

### Constructors

```csharp
// Stream-based (recommended)
public ZArchiveWriter(
    Stream output,
    IZarBlockCompressor? compressor = null,
    IEnumerable<string>? nameOrder = null,
    int maxDegreeOfParallelism = 1,
    Func<IZarBlockCompressor>? compressorFactory = null)

// Callback-based (for advanced scenarios)
public ZArchiveWriter(
    Action<int> newOutputFile,
    Action<byte[], int, int> writeOutputData,
    IZarBlockCompressor? compressor = null,
    IEnumerable<string>? nameOrder = null,
    int maxDegreeOfParallelism = 1,
    Func<IZarBlockCompressor>? compressorFactory = null)
```

`maxDegreeOfParallelism` fans 64 KiB block compression across workers
(byte-identical, v1.1.0); it takes effect only with `compressorFactory`
(one compressor per worker — explicit `IZarBlockCompressor` instances
stay sequential).

### Methods

#### StartNewFile

```csharp
public void StartNewFile(string path)
```

Begins writing a new file entry. Path is relative to the archive root, using `/` or `\` as separators.

**Parameters:**
- `path` — Relative file path (e.g., `"readme.txt"`, `"data/config.json"`)

**Exceptions:**
- `InvalidOperationException` — If already writing a file
- `ArgumentException` — If path is empty or invalid

#### AppendData

```csharp
public void AppendData(ReadOnlySpan<byte> data)
public void AppendData(byte[] data, int offset, int count)
public void AppendData(Stream input)
```

Appends data to the current file. Data is buffered and compressed in 64 KiB blocks.
The `Stream` overload pumps the stream to end (64 KiB takes), so entry streams
need no manual buffering; all three forms produce identical bytes.

**Parameters:**
- `data` — Bytes to append

**Exceptions:**
- `InvalidOperationException` — If no file is being written

#### Finalize

```csharp
public void Finalize()
```

Writes the archive footer and SHA-256 integrity hash. Must be called after all files are written.

**Exceptions:**
- `InvalidOperationException` — If already finalized

#### Dispose

```csharp
public void Dispose()
```

Releases resources. Automatically calls `Finalize()` if not already done.

### Example

```csharp
using var output = File.Create("archive.zar");
using var writer = new ZArchiveWriter(output);

writer.StartNewFile("readme.txt");
writer.AppendData("Hello, World!"u8);

writer.StartNewFile("data/binary.dat");
writer.AppendData(binaryData);

writer.Finalize();
```

---

## ZArchiveReader

Reads `.zar` archive files. Faithful port of `zarchivereader.h`.

### Static Methods

#### TryOpen

```csharp
public static ZArchiveReader? TryOpen(string path)
public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen = false)
```

Opens an archive. Returns `null` on invalid archives (never throws).

**Parameters:**
- `path` — Archive file path
- `stream` — Archive stream
- `leaveOpen` — Keep stream open after reader disposal

**Returns:** Reader instance, or `null` if invalid

**Ownership:** with `leaveOpen: false` (the default), a failed open disposes
the stream — ownership is only transferred to the returned reader on success.
Pass `leaveOpen: true` to keep the stream alive after a failed open.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `InvalidNode` | `uint` | Constant `0xFFFFFFFF` for path-not-found |
| `Dictionary` | `ZstdDictionary?` | Dictionary for dictionary-packed archives (null = plain; inert for plain blocks; dictionary blocks read without it fail, never mis-decode) |

### Methods

#### FileExists

```csharp
public bool FileExists(string path)
```

Checks if a file exists at the given path.

#### DirectoryExists

```csharp
public bool DirectoryExists(string path)
```

Checks if a directory exists at the given path.

#### ReadFile

```csharp
public byte[] ReadFile(string path)
```

Reads and decompresses an entire file.

**Parameters:**
- `path` — File path within the archive

**Returns:** File contents

**Exceptions:**
- `FileNotFoundException` — If file not found
- `InvalidOperationException` — If archive is corrupt

#### ReadFileRange

```csharp
public byte[] ReadFileRange(string path, long offset, long length)
```

Reads a range of bytes from a file.

**Parameters:**
- `path` — File path within the archive
- `offset` — Byte offset within the file
- `length` — Number of bytes to read

**Returns:** Requested byte range

#### ReadDirectory

```csharp
public IReadOnlyList<ZArchiveReader.DirEntry> ReadDirectory(string path)
```

Lists directory contents.

**Parameters:**
- `path` — Directory path within the archive

**Returns:** List of directory entries

#### GetName

```csharp
public string GetName(uint nameIndex)
```

Retrieves a name by its index in the name table.

### DirEntry Structure

```csharp
public readonly struct DirEntry
{
    public string Name { get; }      // Entry name
    public bool IsFile { get; }      // True for files
    public bool IsDirectory { get; } // True for directories
    public ulong Size { get; }       // File size (0 for directories)
}
```

### Thread Safety

The reader is thread-safe for concurrent reads (single lock, like the C++ mutex).

### Example

```csharp
using var reader = ZArchiveReader.TryOpen("archive.zar");
if (reader == null)
{
    Console.WriteLine("Invalid archive");
    return;
}

// List root directory
foreach (var entry in reader.ReadDirectory("/"))
{
    Console.WriteLine($"{entry.Name}: {(entry.IsFile ? $"{entry.Size} bytes" : "DIR")}");
}

// Read a file
byte[] data = reader.ReadFile("readme.txt");
```

---

## ZArchiveTool

High-level pack/extract operations. Port of `main.cpp` CLI behavior.

### Static Methods

#### Pack

```csharp
public static void Pack(
    string inputDirectory,
    string? outputFile = null,
    Action<string>? progress = null,
    IZarBlockCompressor? compressor = null,
    bool deterministicOrder = true)
```

Packs a directory into a `.zar` file.

**Parameters:**
- `inputDirectory` — Directory to pack (recursively)
- `outputFile` — Destination path, or `null` for `<stem>.zar`
- `progress` — Optional per-file callback (relative path)
- `compressor` — Block compressor, or `null` for default (zstd level 6)
- `deterministicOrder` — `true` (default) sorts entries ordinally

**Exceptions:**
- `IOException` — On I/O errors or when refusing to overwrite
- `InvalidOperationException` — On archive structure errors

#### Extract

```csharp
public static void Extract(string inputFile, string outputDirectory)
```

Extracts an archive to a directory.

**Parameters:**
- `inputFile` — Archive file path
- `outputDirectory` — Destination directory (created if needed)

**Exceptions:**
- `IOException` — On I/O errors
- `InvalidOperationException` — On corrupt archives

---

## ZarPipelineOptions (ZArchiveSharp.Pipeline)

Options for `ZarPipeline` pack/extract work (also honored by the `zar`
CLI flags `--level`, `--check`, `--dict`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Level` | `int` | `6` | zstd level 1–22 for packing |
| `Checksum` | `bool` | `false` | Write per-block content checksums |
| `Dictionary` | `ZstdDictionary?` | `null` | Pack dictionary frames / extract them (never stored in the archive; inert for plain frames; ignored when `Compressor` is set) |
| `Compressor` | `IZarBlockCompressor?` | `null` | Explicit block compressor; `null` builds one from `Level`/`Checksum`/`Dictionary` (explicit instances keep the sequential path) |
| `CollisionPolicy` | `ZarCollisionPolicy` | `Fail` | What to do when the output path already exists |
| `MaxDegreeOfParallelism` | `int` | `4` | Batch parallelism, plus the 64 KiB block fan-out inside a single pack/extract |
| `DeterministicOrder` | `bool` | `true` | Sort entries ordinally before packing |
| `DeleteSourceOnSuccess` | `bool` | `false` | Delete the pack source directory after a successful pack (single and batch) |
| `Pause` | `PauseToken` | default | Pause gate checked alongside the cancellation token |
| `NameOrder` | `IReadOnlyList<string>?` | `null` | Pre-seeded name-table order; `null` = pack order (first appearance). Set to a source-walk (discovery) order for byte-parity with packers that write names in discovery order |

`ZarPipeline.Pack` validates and collects the source *before* resolving the
output, so an `Overwrite` policy never deletes a previous archive when the
pack cannot start; `ZarPipeline.PackSource` honors the collision policy and
creates the output directory.

---

## IZarBlockCompressor

Interface for custom block compressors.

```csharp
public interface IZarBlockCompressor
{
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
}
```

**Returns:** Compressed size, or `-1` to store the block raw (uncompressed).

### ZarRawCompressor

Built-in compressor that stores every block raw (no compression).

```csharp
public sealed class ZarRawCompressor : IZarBlockCompressor
```

---

## ZarPackEngine (ZArchiveSharp.Pipeline)

Engine behind `ZarPipeline` and the callable CLI runners: collision
resolution, pack, and the hardened extraction path.

```csharp
public static class ZarPackEngine
{
    // Prefix of the IOException thrown when the Fail policy refuses.
    // Batch callers match it to separate -11 refusals from other faults.
    public const string OutputExistsMessage = "The output file already exists:";

    // Maximum accepted archive directory nesting during extraction.
    public const int MaxExtractDepth = 1024;

    public static string? ResolveOutputPath(string wantedPath, ZarCollisionPolicy policy);
    public static string? MoveIntoPlace(
        string source, string wantedPath, ZarCollisionPolicy policy, bool isDirectory);

    public static string PackEntries(
        IReadOnlyList<ZarPackEntry> entries, string displayPath, string zarPath,
        ZarPipelineOptions? options = null, IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ZarCollisionPolicy collisionPolicy = ZarCollisionPolicy.Fail);

    public static IReadOnlyList<string> ExtractEntries(
        string zarPath, string destDir, string? displayPath = null,
        ZarPipelineOptions? options = null, IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<string>? log = null);

    public static IReadOnlyList<string> ExtractOpen(
        ZArchiveReader reader, string displayPath, string destDir,
        ZarPipelineOptions? options = null, IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default, Action<string>? log = null);
}
```

**Collision resolution.** `ResolveOutputPath` returns the path to use, or
`null` for `Skip`; `Fail` throws `IOException` prefixed with
`OutputExistsMessage`. `MoveIntoPlace` moves a staged file/directory into
place and re-resolves when the free name is claimed between resolve and move,
keeping `AutoRename` suffixes canonical (`game`, `game_1`, …).
`PackEntries` returns the path actually written (which can differ from
`zarPath` after an `AutoRename` race) and deletes its partial output on
failure.

**Extraction safety.** Entry names are validated (no `..`, separators,
rooted/drive-qualified paths, or Windows device names), the resolved path is
re-checked against the destination root, nesting deeper than
`MaxExtractDepth` fails catchably, and files are written through unique
`.part` scratch files and moved into place only after the size check.

---

## PauseTokenSource (ZArchiveSharp.Pipeline)

Pause gate shared by pipeline workers. `PauseToken` is a snapshot struct; a
default value never pauses. `PauseTokenSource` implements `IDisposable` since
v1.2.0 — dispose it after the workers stop, while no `PauseToken` from it can
still be waited on.

```csharp
public sealed class PauseTokenSource : IDisposable
{
    public bool IsPaused { get; }
    public PauseToken Token { get; }
    public void Pause();
    public void Resume();
    public void Dispose();
}
```

---

## ZstdCompressionOptions (ZArchiveSharp.Zstd)

Options for the zstd compressor.

### Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Level` | `int` | `6` | Compression level (1–22) |
| `ChecksumFlag` | `bool` | `false` | Write 4-byte XXH64 content checksum |
| `Dictionary` | `ZstdDictionary?` | `null` | Dictionary history (`null` = plain frames) |

### Static Methods

```csharp
public static ZstdCompressionOptions FromLevel(int level)
```

Creates options for the specified level (1–22).

---

## ZstdCompressor (ZArchiveSharp.Zstd)

Pure-C# zstd encoder. Implements `IZarBlockCompressor`.

### Constructor

```csharp
public ZstdCompressor(ZstdCompressionOptions? options = null)
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Options` | `ZstdCompressionOptions` | Active options |

### Methods

#### Compress

```csharp
public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
```

Compresses source as a single-shot frame. Returns frame size, or `-1` when the frame would not fit or would not be smaller.

#### CompressBlock

```csharp
public byte[] CompressBlock(ReadOnlySpan<byte> source)
```

Compresses and returns the frame as a new byte array.

#### GetCompressBound

```csharp
public static int GetCompressBound(int sourceSize)
```

Returns the maximum possible compressed size for a given input size.

#### DecompressFrame

```csharp
public static byte[] DecompressFrame(ReadOnlySpan<byte> src, int maxSize)
```

Decompresses a zstd frame. Since v1.2.0 the cap is enforced *during* decoding
(the decoder's `MaxFrameContentSize` is set to `maxSize`), so an oversized
frame is rejected before a large buffer is materialized. `maxSize: 0` decodes
under a one-byte cap, so only an empty frame passes.

**Parameters:**
- `src` — Frame bytes
- `maxSize` — Maximum allowed decompressed size

**Returns:** Decompressed data

---

## ZstdDecompressor (ZArchiveSharp.Zstd)

Stateless decoder entry points over concatenated frames, with optional
dictionary and explicit resource limits.

```csharp
public static class ZstdDecompressor
{
    public static byte[] Decompress(byte[] src);
    public static byte[] Decompress(byte[] src, ZstdDecoderOptions options);
    public static byte[] Decompress(byte[] src, int offset, int length);
    public static byte[] Decompress(byte[] src, int offset, int length, ZstdDecoderOptions options);
    public static byte[] Decompress(byte[] src, ZstdDictionary? dict);
    public static byte[] Decompress(byte[] src, int offset, int length, ZstdDictionary? dict);
    public static byte[] Decompress(
        byte[] src, int offset, int length, ZstdDictionary? dict, ZstdDecoderOptions options);

    public static void DecompressExact(
        byte[] src, int srcOffset, int srcLength,
        byte[] dst, int dstOffset, int dstLength); // + options / dict overloads
}
```

Since v1.2.0 every overload validates `src`/`dst`/`options` and both ranges
up front (`ArgumentNullException` / `ArgumentOutOfRangeException`) and
enforces a cumulative output cap across concatenated frames (see
`MaxTotalOutputSize`). Corruption, dictionary mismatch, and cap violations
throw `ZstdException`.

### ZstdDecoderOptions

Decoder resource limits (all configurable; defaults accept foreign frames up
to 512 MiB and cap one `Decompress` call at 1 GiB total):

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `MaxWindowSize` | `ulong` | 512 MiB | Reject frames declaring a larger window |
| `MaxFrameContentSize` | `ulong` | 512 MiB | Bound allocation for frames without a declared content size |
| `MaxTotalOutputSize` | `ulong` | 1 GiB | Cumulative output cap across all frames of one `Decompress` call; set to `ulong.MaxValue` to disable |

```csharp
var options = new ZstdDecoderOptions { MaxTotalOutputSize = 64UL * 1024 * 1024 };
byte[] data = ZstdDecompressor.Decompress(frame, options);
```

---

## ZstdCompressionStream (ZArchiveSharp.Zstd)

Write-only zstd compression stream. Buffers everything written and emits one
logical frame with an unknown-size header on `Dispose()` - byte-identical to
encoding the concatenated input in one shot. `Flush()` emits the 6-byte frame
header once payload exists - except with a dictionary, where the header carries
the final content size and nothing is emitted before `Dispose()`. Not seekable;
async is thin-over-sync.

```csharp
public ZstdCompressionStream(Stream destination, int level = 6, bool checksum = false, bool leaveOpen = false)
public ZstdCompressionStream(Stream destination, ZstdCompressionOptions options, bool leaveOpen = false)
```

**Exceptions:**
- `ArgumentNullException` — Destination or options is null
- `ArgumentOutOfRangeException` — Level outside 1–22
- `ObjectDisposedException` — Write after dispose
- `NotSupportedException` — Read/Seek/SetLength/Length/Position

### Example

```csharp
using var dest = File.Create("data.zst");
using (var enc = new ZstdCompressionStream(dest, level: 6, checksum: true))
{
    await source.CopyToAsync(enc);
} // frame finalized here

---

## ZstdDecompressionStream (ZArchiveSharp.Zstd)

Read-only zstd decompression stream over concatenated frames (skippable frames
skipped). Decodes incrementally through the shared block path with
`ZstdDecoderOptions` caps enforced per frame. Not seekable; async is
thin-over-sync.

```csharp
public ZstdDecompressionStream(Stream compressed, bool leaveOpen = false)
public ZstdDecompressionStream(Stream compressed, ZstdDecoderOptions options, bool leaveOpen = false)
```

**Exceptions:**
- `ArgumentNullException` — Source or options is null
- `ZstdException` — Corrupt/truncated input, cap exceeded, checksum mismatch
- `NotSupportedException` — Write/Seek/SetLength/Length/Position

### Example

```csharp
using var src = File.OpenRead("data.zst");
using var dec = new ZstdDecompressionStream(src);
using var outMs = new MemoryStream();
await dec.CopyToAsync(outMs);
```

---

## ZstdDictionary (ZArchiveSharp.Zstd)

Immutable, thread-safe reusable zstd dictionary (use only; training is out
of scope). A supplied dictionary is always active per frame — history plus,
for formatted dictionaries, initial tables; frames carrying a dictionary ID
require it to match `DictId`.

```csharp
public static ZstdDictionary FromBytes(byte[] dict) // auto-detect formatted vs raw
public static ZstdDictionary FromRawPrefix(ReadOnlySpan<byte> prefix, uint dictId = 0)
```

| Property | Type | Description |
|----------|------|-------------|
| `DictId` | `int` | Dictionary ID (0 = no ID field; compared as a 32-bit pattern) |
| `IsFormatted` | `bool` | True when loaded from magic-headed bytes |
| `ContentSize` | `int` | History content size in bytes |

Dictionary-aware entry points (all additive; `null` = today's behavior):

```csharp
// ZstdCompressionOptions
public ZstdDictionary? Dictionary { get; init; }

// ZstdCompressor
public byte[] CompressBlock(ReadOnlySpan<byte> source, ZstdDictionary? dict);

// ZstdDecompressor
public static byte[] Decompress(byte[] src, ZstdDictionary? dict);
public static byte[] Decompress(byte[] src, int offset, int length, ZstdDictionary? dict);
public static byte[] Decompress(byte[] src, int offset, int length, ZstdDictionary? dict, ZstdDecoderOptions options);

// ZstdCompressionStream: options may carry Dictionary (header deferred to Dispose)
// ZstdDecompressionStream
public ZstdDecompressionStream(Stream compressed, ZstdDecoderOptions options, ZstdDictionary? dict, bool leaveOpen = false);
```

### Example

```csharp
var dict = ZstdDictionary.FromBytes(File.ReadAllBytes("words.dict"));
var options = new ZstdCompressionOptions { Level = 6, Dictionary = dict };
byte[] frame = new ZstdCompressor(options).CompressBlock(data);
byte[] back = ZstdDecompressor.Decompress(frame, dict);
```

---

## ZstdCli (ZArchiveSharp.Pipeline)

Callable form of the `zar zstd` contract (single zstd streams, not
archives). Failures map onto the `ZarchiveCli` exit-code table (no new
codes); cancellation propagates `OperationCanceledException`.

```csharp
public static bool TryParse(string[] args, out ZstdJob? job, out string? error,
    int defaultLevel = 6, string? defaultDictPath = null,
    bool defaultChecksum = false, bool defaultQuiet = false, bool defaultStdout = false);
public static Task<int> RunAsync(ZstdJob job, Stream stdin, Stream stdout,
    Action<string>? log, Action<string> error, CancellationToken ct = default);
```

`TryParse` takes the tokens after `zstd` (`-c/--compress`, `-d/--decompress`
with exactly one required, `-l/--level`, `--dict`, `--stdout`, `--check` /
`--no-check`, `-q/--quiet`, `-h/--help`) and never throws. `RunAsync` opens
file paths (null = the given stdin/stdout streams, flushed but never
closed), deletes a created file output when the run fails, and sends
failures to `error`.

---

## SeekableCli (ZArchiveSharp.Pipeline)

Callable form of the `zar seekable` contract (seekable zstd files:
compress, decompress with byte/frame slicing, and seek-table listing).
Same conventions as `ZstdCli` (exit-code reuse, cancellation, partial-output
cleanup).

```csharp
public static bool TryParse(string[] args, out SeekableJob? job, out string? error,
    int defaultLevel = 3, bool? defaultChecksum = null,
    bool defaultQuiet = false, bool defaultStdout = false);
public static Task<int> RunAsync(SeekableJob job, Stream stdin, Stream stdout,
    Action<string>? log, Action<string> error, CancellationToken ct = default);
public static bool TryParseByteSize(string? value, out ulong size, out string? error);
```

`TryParse` takes the tokens after `seekable`, verb first (`compress|c`,
`decompress|d`, `list|l`): compress takes `-l/--level` (1–22, default 3),
`-s/--frame-size` (`TryParseByteSize` syntax, default 2M, capped at 1G),
`--frame-size-policy`, `--checksum/--no-checksum` (default on),
`--seek-table-file`; decompress takes `--from/--to` (`end`),
`--from-frame/--to-frame` (`last`), `--seek-table-file`; list takes
`--from-frame/--to-frame/--num-frames`, `-d/--detail`,
`--seek-table-format foot|head`. Compress/decompress share `-f/--force`,
`-c/--stdout`, `-q/--quiet` (ignored by list). The verb validates its own
options, so a misplaced flag errors instead of being ignored. Compress with a
file input and no output path derives `<input>.zst`; decompress defaults to
stdout.

---

## SevenZip (ZArchiveSharp.Pipeline)

Archive-container stage: finds an external 7z binary and extracts
7z stays external by design).

```csharp
public static string? FindTool(string? preferredPath = null,
    IEnumerable<string>? searchDirectories = null, bool probeWellKnownLocations = true);
public static void Extract(string archivePath, string destDir, string? toolPath = null,
    IProgress<double>? progress = null, PauseToken pause = default,
    CancellationToken cancellationToken = default);
public static string? PickIsoCandidate(IEnumerable<string> extractedFiles);
```

`FindTool` checks the explicit path, then the standard Windows install
location, then `searchDirectories` (default: `PATH`) for `7z`/`7zz`.
`Extract` runs `x "archive" -o"dest" -y -bsp1` with full paths preserved
(the oracle's flat `e` would mangle directory trees). `PickIsoCandidate`
returns the first `.iso` in ordinal order (extension case-insensitive) or
null — when several ISOs are present the rest are ignored, like the oracle.

---

## ZstdStrategy (ZArchiveSharp.Zstd)

Compression strategy selector. Maps to libzstd's `ZSTD_strategy`.

| Value | Name | Typical Levels |
|-------|------|----------------|
| `1` | `Fast` | 1 |
| `2` | `DoubleFast` | 2–3 |
| `3` | `Greedy` | 4–5 |
| `4` | `Lazy` | 6–7 |
| `5` | `Lazy2` | 8–9 |
| `6` | `BtLazy2` | 10–12 |
| `7` | `BtOpt` | 13–15 |
| `8` | `BtUltra` | 16–18 |
| `9` | `BtUltra2` | 19–22 |

---

## SeekableWriter (ZArchiveSharp.Seekable)

Writes seekable zstd files with Foot/Head seek tables.

### Constructor

```csharp
public SeekableWriter(SeekableOptions? options = null)
```

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `SeekTable` | `SeekTable` | Current seek table (frames logged so far) |

### Methods

#### Write

```csharp
public void Write(ReadOnlySpan<byte> data)
public void Write(Stream input)
```

Appends data, emitting full frames as needed. The `Stream` overload pumps in
128 KiB takes, so regular files frame exactly like one span write and like the
oracle CLI; short-read streams can shift `Compressed`-policy boundaries (like
odd oracle reads would) while `Uncompressed` boundaries never move — every
framing decodes identically. A frame never exceeds the format's 1 GiB
uncompressed cap: the `Compressed` policy ends a frame when the compressed
threshold is reached *or* when the uncompressed cap is hit.

#### Finish

```csharp
public byte[] Finish()
```

Finalizes and returns the complete seekable file bytes.

#### FinishHead

```csharp
public (byte[] Data, byte[] SeekTable) FinishHead()
```

Returns the bare frame data plus the standalone `Head` seek table: `Data` is
the frames without an appended `Foot`, `SeekTable` is the serialized table.

---

## SeekableReader (ZArchiveSharp.Seekable)

Reads seekable zstd files.

### Constructors

```csharp
// Parse embedded Foot table
public SeekableReader(byte[] data)

// Use external seek table (e.g., standalone Head; table must not be null)
public SeekableReader(byte[] data, SeekTable table)

// Stream-backed: parses the Foot from the tail, reads frames on demand
// (multi-GB files never sit fully in memory; stream not owned)
public SeekableReader(Stream stream)
public SeekableReader(Stream stream, SeekTable table)
```

The stream must be readable and seekable and stay open for the reader's
lifetime; decode results are identical to the byte-array constructors.

### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Table` | `SeekTable` | Parsed seek table |
| `DecompressedLength` | `long` | Total decompressed size |
| `FrameCount` | `int` | Number of frames |

### Methods

#### DecompressAll

```csharp
public byte[] DecompressAll()
```

Decompresses the entire payload.

#### DecompressRange

```csharp
public byte[] DecompressRange(long offset, long length)
```

Decompresses a byte range, decoding only the frames the range touches.

**Exceptions:**
- `ArgumentOutOfRangeException` — negative `offset`/`length`, a range past
  `DecompressedLength`, or a range larger than `int.MaxValue` (it cannot be
  materialized as one array). Since v1.2.0 these are
  `ArgumentOutOfRangeException` (previously `ZstdException`).

#### DecompressFrames

```csharp
public byte[] DecompressFrames(int first, int lastInclusive)
```

Decompresses frames `first` through `lastInclusive` concatenated
(`set_lower_frame` / `set_upper_frame`).

**Exceptions:**
- `ArgumentOutOfRangeException` — negative indices, `lastInclusive < first`,
  or `lastInclusive >= FrameCount` (since v1.2.0; previously `ZstdException`).

---

## Error Handling

### Exception Types

| Exception | Namespace | When Thrown |
|-----------|-----------|------------|
| `ZarArchiveOpenException` | `ZArchiveSharp.Pipeline` | Archive fails to open |
| `ZarInputOpenException` | `ZArchiveSharp.Pipeline` | Input file cannot be opened |
| `ZarEntryCreateException` | `ZArchiveSharp.Pipeline` | Archive entry creation fails |
| `ZstdException` | `ZArchiveSharp.Zstd` | zstd decompression error |
| `IOException` | `System` | I/O errors |
| `InvalidOperationException` | `System` | Invalid state (corrupt archive, etc.) |

### Handling Corrupt Archives

```csharp
try
{
    ZArchiveTool.Extract("corrupt.zar", "output");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine($"Corrupt archive: {ex.Message}");
}
catch (IOException ex)
{
    Console.WriteLine($"I/O error: {ex.Message}");
}
```

### Null-Safe Reader Pattern

```csharp
// TryOpen never throws — returns null on invalid archives
using var reader = ZArchiveReader.TryOpen("maybe-valid.zar");
if (reader == null)
{
    Console.WriteLine("Invalid or corrupt archive");
    return;
}
```
