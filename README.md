# ZARSharp

[![CI](https://github.com/purelogiccode/ZARSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/purelogiccode/ZARSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ZARSharp.svg)](https://www.nuget.org/packages/ZARSharp)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Pure-C# port of the [ZArchive 0.1.2](https://github.com/unknownbrackets/ZArchive) library** — directory-tree archives with per-block zstd compression. Zero native dependencies, BCL only; trimmable and AOT-compatible (`net8.0` / `net9.0` / `net10.0`).

## Features

- **Byte-identical output** to the original C++ `zarchive.exe` and libzstd 1.5.7
- **Full RFC 8878 zstd encoder & decoder** (levels 1–22, all 9 strategies) — no native dependencies
- **Streaming API** — `ZstdCompressionStream` / `ZstdDecompressionStream` (chunk-size free, 10 MiB round-trips tested)
- **Dictionary use** — compress/decompress with supplied dicts (formatted + raw prefix); `ZstdDictionary`, per-file 4× smaller small entries (training out of scope)
- **At native speed on the hot path** — L6 64 KiB ≈1.0× libzstd 1.5.7, decode ≈1.0× (measured; see [Benchmarks](docs/benchmarks.md))
- **Seekable zstd format** (Foot + Head) — zeekstd-compatible framing
- **Pipeline engine** — parallel batch pack/extract with progress, pause, cancellation & collision policies
- **CLI tool** — `zar` command matching `zarchive.exe` exit codes and behavior
- **Trimmable & AOT-compatible** — works with Native AOT deployment
- **Zero runtime dependencies** — BCL only, no `unsafe` code in the zstd path

## Quick Start

### Install

```bash
dotnet add package ZARSharp
```

### Pack & Extract

```csharp
using ZARSharp;

// Pack a directory (each 64 KiB block zstd level 6 by default)
ZArchiveTool.Pack(@"C:\game", @"C:\game.zar");

// Extract it back
ZArchiveTool.Extract(@"C:\game.zar", @"C:\game_out");
```

### Low-Level Writer / Reader

```csharp
using ZARSharp;
using ZARSharp.Zstd;

// Write an archive
using var output = File.Create("game.zar");
using var writer = new ZArchiveWriter(output); // default: ZstdCompressor level 6
writer.StartNewFile("readme.txt");
writer.AppendData("hello"u8);
writer.Finalize();

// Read it back
using var reader = ZArchiveReader.TryOpen("game.zar");
```

### Standalone zstd Compression

```csharp
using ZARSharp.Zstd;

// Compress (byte-identical to libzstd)
var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
byte[] frame = compressor.CompressBlock(data); // single-shot, any size

// Decompress
byte[] back = ZstdCompressor.DecompressFrame(frame, maxSize: data.Length);
```

### Streams & Dictionaries

```csharp
using ZARSharp.Zstd;

// Stream a file through zstd (any chunk size; flushed, never closed)
using var input = File.OpenRead("big.bin");
using var output = File.Create("big.zst");
using var enc = new ZstdCompressionStream(output, level: 6);
input.CopyTo(enc);

// Compress small entries with a dictionary (4× smaller here)
var dict = ZstdDictionary.FromRawPrefix(prefixBytes);
var opts = new ZstdCompressionOptions { Level = 6, Dictionary = dict };
byte[] small = new ZstdCompressor(opts).CompressBlock(entry);
byte[] orig = ZstdDecompressor.Decompress(small, dict);
```

### CLI Tool

```bash
# Install as a global tool
dotnet tool install -g ZARSharp.Cli

# Pack a directory
zar <directory> [output.zar]

# Extract an archive
zar <archive.zar> [output_dir]

# Convert XISO to .zar
zar --iso <game.iso> [output.zar]

# Raw zstd files (stdin/stdout by default, pipes compose)
zar zstd --compress big.bin big.zst
zar zstd --decompress big.zst big.bin
zar zstd -c big.bin | zar zstd -d > big.bin

# Archives with dictionaries + checksums
zar --dict words.dict --check <directory> [output.zar]
```

## Projects

| Project | Description |
|---------|-------------|
| **ZARSharp** | Core library — archive reader/writer, zstd codec, seekable format, pipeline |
| **ZARSharp.Cli** | Command-line tool (`zar`) — pack, extract, convert, batch operations |
| **ZARSharp.Benchmarks** | BenchmarkDotNet performance suite |
| **ZARSharp.Tests** | Comprehensive test suite (4023 tests, parity validation) |

## Documentation

- **[Getting Started](docs/getting-started.md)** — Installation, setup, and first steps
- **[API Reference](docs/api-reference.md)** — Complete library API documentation
- **[CLI Reference](docs/cli-reference.md)** — Command-line tool usage and options
- **[Zstd Compression](docs/zstd-compression.md)** — Compression levels, strategies, and tuning
- **[ZAR Format](docs/zar-format.md)** — Archive format specification and internals
- **[Seekable Format](docs/seekable-format.md)** — Seekable zstd framing (Foot + Head)
- **[Pipeline](docs/pipeline.md)** — Batch operations, progress, and collision handling
- **[Benchmarks](docs/benchmarks.md)** — Performance characteristics and tuning
- **[FAQ](docs/faq.md)** — Frequently asked questions

## Byte-Identity Target

The encoder, archive container and seekable framing are **byte-identical** to the frozen references (libzstd 1.5.7, zeekstd). The test suite proves it against native tools on thousands of vectors, and `ZARSharp.Tests/Goldens/` pins native bytes so CI holds the line with no toolchain installed.

Two known boundaries:
- The shipped `zarchive.exe` bundles libzstd 1.5.2, whose level 6 can differ from 1.5.7 on multi-transition hetero 64 KiB blocks. Our frames follow the frozen 1.5.7.
- The reference C seekable library writes plain zstd frames while zeekstd sets the frame content-checksum flag. Both flavors are valid; our reader decodes both, our writer emits the zeekstd flavor.

## Limits

- No dictionary *training* (use only), no legacy frames, no multithreading inside one frame
- Decoder caps (configurable): 512 MiB window, 512 MiB frame content
- Corrupt archives throw documented exceptions; truncations always fail the open

## Requirements

- .NET 8.0, 9.0, or 10.0
- No native dependencies

## License

MIT — see [LICENSE](LICENSE).

## Contributing

Contributions are welcome! Please see the [issue tracker](https://github.com/purelogiccode/ZARSharp/issues) for known issues and feature requests.
