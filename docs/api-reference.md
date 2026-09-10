# API Reference

Complete API documentation for ZARSharp. All types are in the `ZARSharp` namespace unless otherwise noted.

## Namespaces

| Namespace | Contents |
|-----------|----------|
| `ZARSharp` | Archive reader/writer, format structures, tool |
| `ZARSharp.Zstd` | zstd encoder/decoder, compression options |
| `ZARSharp.Seekable` | Seekable zstd format (Foot + Head) |
| `ZARSharp.Pipeline` | Pipeline engine, batch operations, progress |

---

## ZArchiveWriter

Writes `.zar` archive files. Faithful port of `zarchivewriter.cpp`.

### Constructors

```csharp
// Stream-based (recommended)
public ZArchiveWriter(Stream output, IZarBlockCompressor? compressor = null)

// Callback-based (for advanced scenarios)
public ZArchiveWriter(
    Action<int> newOutputFile,
    Action<byte[], int, int> writeOutputData,
    IZarBlockCompressor? compressor = null)
```

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
```

Appends data to the current file. Data is buffered and compressed in 64 KiB blocks.

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

## ZarPipelineOptions (ZARSharp.Pipeline)

Options for `ZarPipeline` pack/extract work (also honored by the `zar`
CLI flags `--level`, `--check`, `--dict`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Level` | `int` | `6` | zstd level 1–22 for packing |
| `Checksum` | `bool` | `false` | Write per-block content checksums |
| `Dictionary` | `ZstdDictionary?` | `null` | Pack dictionary frames / extract them (never stored in the archive; inert for plain frames; ignored when `Compressor` is set) |
| `CollisionPolicy` | `ZarCollisionPolicy` | `Fail` | What to do when the output path already exists |
| `MaxDegreeOfParallelism` | `int` | `4` | Batch parallelism |
| `DeterministicOrder` | `bool` | `true` | Sort entries ordinally before packing |

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

## ZstdCompressionOptions (ZARSharp.Zstd)

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

## ZstdCompressor (ZARSharp.Zstd)

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

Decompresses a zstd frame.

**Parameters:**
- `src` — Frame bytes
- `maxSize` — Maximum allowed decompressed size

**Returns:** Decompressed data

---

## ZstdCompressionStream (ZARSharp.Zstd)

Write-only zstd compression stream. Buffers everything written and emits one
logical frame with an unknown-size header on `Dispose()` — byte-identical to
encoding the concatenated input in one shot. `Flush()` emits the 6-byte frame
header once payload exists. Not seekable; async is thin-over-sync.

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

## ZstdDecompressionStream (ZARSharp.Zstd)

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

## ZstdDictionary (ZARSharp.Zstd)

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

## ZstdCli (ZARSharp.Pipeline)

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

## ZstdStrategy (ZARSharp.Zstd)

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

## SeekableWriter (ZARSharp.Seekable)

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
```

Appends data, emitting full frames as needed.

#### Finish

```csharp
public byte[] Finish()
```

Finalizes and returns the complete seekable file bytes.

#### FinishHead

```csharp
public byte[] FinishHead()
```

Returns just the seek table as a standalone Head frame.

---

## SeekableReader (ZARSharp.Seekable)

Reads seekable zstd files.

### Constructors

```csharp
// Parse embedded Foot table
public SeekableReader(byte[] data)

// Use external seek table (e.g., standalone Head)
public SeekableReader(byte[] data, SeekTable table)
```

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

---

## Error Handling

### Exception Types

| Exception | Namespace | When Thrown |
|-----------|-----------|------------|
| `ZarArchiveOpenException` | `ZARSharp.Pipeline` | Archive fails to open |
| `ZarInputOpenException` | `ZARSharp.Pipeline` | Input file cannot be opened |
| `ZarEntryCreateException` | `ZARSharp.Pipeline` | Archive entry creation fails |
| `ZstdException` | `ZARSharp.Zstd` | zstd decompression error |
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
