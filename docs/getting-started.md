# Getting Started

This guide covers installing ZArchiveSharp and performing basic archive operations.

## Prerequisites

- .NET 8.0, 9.0, or 10.0 SDK
- No native dependencies required

## Installation

### Library (NuGet Package)

```bash
dotnet add package ZArchiveSharp
```

Or add to your `.csproj`:

```xml
<PackageReference Include="ZArchiveSharp" Version="*" />
```

### CLI Tool (Global Tool)

```bash
dotnet tool install -g ZArchiveSharp.Cli
```

Verify installation:

```bash
zar --version
```

### From Source

```bash
git clone https://github.com/purelogiccode/ZArchiveSharp.git
cd CSharp_ZArchiveSharp
dotnet build -c Release
```

---

## Packing a Directory

### High-Level API (Recommended)

The simplest way to create an archive:

```csharp
using ZArchiveSharp;

// Pack with default settings (zstd level 6, deterministic order)
ZArchiveTool.Pack(@"C:\mydata", @"C:\mydata.zar");
```

### With Progress Reporting

```csharp
using ZArchiveSharp;

ZArchiveTool.Pack(
    inputDirectory: @"C:\mydata",
    outputFile: @"C:\mydata.zar",
    progress: file => Console.WriteLine($"Packing: {file}")
);
```

### Custom Compression Level

```csharp
using ZArchiveSharp;
using ZArchiveSharp.Zstd;

var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(9));
ZArchiveTool.Pack(@"C:\mydata", @"C:\mydata.zar", compressor: compressor);
```

### Raw Storage (No Compression)

```csharp
using ZArchiveSharp;

ZArchiveTool.Pack(
    @"C:\mydata",
    @"C:\mydata.zar",
    compressor: new ZarRawCompressor()
);
```

### Low-Level Writer API

For fine-grained control over archive construction:

```csharp
using ZArchiveSharp;
using ZArchiveSharp.Zstd;

using var output = File.Create("game.zar");
using var writer = new ZArchiveWriter(output);

// Add files
writer.StartNewFile("readme.txt");
writer.AppendData("Hello, World!"u8);

writer.StartNewFile("data/config.json");
writer.AppendData("""{"key": "value"}"""u8);

// Finalize (writes footer and SHA-256)
writer.Finalize();
```

---

## Extracting an Archive

### High-Level API

```csharp
using ZArchiveSharp;

ZArchiveTool.Extract(@"C:\mydata.zar", @"C:\extracted");
```

### Low-Level Reader API

```csharp
using ZArchiveSharp;

// TryOpen returns null on invalid archives (never throws)
using var reader = ZArchiveReader.TryOpen("game.zar");
if (reader == null)
{
    Console.WriteLine("Invalid archive");
    return;
}

// List directory contents
foreach (var entry in reader.ReadDirectory("/"))
{
    Console.WriteLine($"{(entry.IsFile ? "F" : "D")} {entry.Name} ({entry.Size} bytes)");
}

// Read a file
var data = reader.ReadFile("readme.txt");
Console.WriteLine(System.Text.Encoding.UTF8.GetString(data));
```

---

## Using the Pipeline API

The pipeline API provides batch operations with progress, pause, cancellation, and collision handling:

```csharp
using ZArchiveSharp;
using ZArchiveSharp.Pipeline;

var options = new ZarPipelineOptions
{
    Level = 6,
    CollisionPolicy = ZarCollisionPolicy.AutoRename,
    MaxDegreeOfParallelism = 4,
};

var progress = new Progress<ZarProgress>(p =>
{
    Console.WriteLine($"[{p.Operation}] {p.CurrentFile} - {p.Ratio:P1}");
});

// Pack with options
string? result = ZarPipeline.Pack(
    @"C:\mydata",
    @"C:\output.zar",
    options,
    progress
);

// Extract
var files = ZarPipeline.Extract(@"C:\output.zar", @"C:\extracted");
```

---

## Standalone zstd Compression

ZArchiveSharp includes a complete RFC 8878 zstd encoder and decoder:

### Compress

```csharp
using ZArchiveSharp.Zstd;

var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
byte[] frame = compressor.CompressBlock(data);

// Or use Span<byte> for zero-alloc
int written = compressor.Compress(sourceSpan, destSpan);
if (written == -1)
{
    // Frame would not fit or would not compress — store raw
}
```

### Decompress

```csharp
using ZArchiveSharp.Zstd;

byte[] decompressed = ZstdCompressor.DecompressFrame(frameBytes, maxSize: 1024 * 1024);

// Or with options
var options = new ZstdDecoderOptions { MaxWindowSize = 512 * 1024 * 1024 };
var decoder = new ZstdDecompressor(options);
decoder.DecompressFrame(sourceSpan, destSpan);
```

---

## Next Steps

- **[CLI Reference](cli-reference.md)** — Learn all command-line options
- **[API Reference](api-reference.md)** — Complete API documentation
- **[Zstd Compression](zstd-compression.md)** — Tune compression for your use case
- **[Pipeline](pipeline.md)** — Advanced batch operations
