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
