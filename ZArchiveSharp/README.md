# ZArchiveSharp

Pure-C# port of the [ZArchive 0.1.2](https://github.com/unknownbrackets/ZArchive)
library: directory-tree archives with per-block zstd compression. No native
dependencies, BCL only; trimmable and AOT-compatible (`net8.0`/`net9.0`/`net10.0`).

## Install

```bash
dotnet add package ZArchiveSharp
```

## Layout

```csharp
// Pack a directory (each 64 KiB block zstd level 6 by default).
ZArchiveTool.Pack(@"C:\game", @"C:\game.zar");

// Extract it back.
ZArchiveTool.Extract(@"C:\game.zar", @"C:\game_out");
```

```csharp
// Low-level writer/reader with an explicit compressor choice.
using var output = File.Create("game.zar");
using var writer = new ZArchiveWriter(output); // default: ZstdCompressor level 6
writer.StartNewFile("readme.txt");
writer.AppendData("hello"u8);
writer.Finalize();

using var reader = ZArchiveReader.TryOpen("game.zar");
```

```csharp
// Standalone zstd frames (RFC 8878), levels 1-22, byte-identical to libzstd.
var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
byte[] frame = compressor.CompressBlock(data); // single-shot, any size
byte[] back = ZstdCompressor.DecompressFrame(frame, maxSize: data.Length);
```

```csharp
// Batch pipeline with progress, pause, cancellation and collision policies.
var results = ZarPipeline.PackBatch(dirs, destDir, new ZarPipelineOptions
{
    MaxDegreeOfParallelism = 4,
    CollisionPolicy = ZarCollisionPolicy.AutoRename,
}, progress);

// Callable zarchive.exe contract (same defaults, messages and exit codes).
int code = ZarchiveCli.Run(["input_dir", "out.zar"], log: Console.WriteLine);
```

```csharp
// Seekable zstd (Foot + Head), zeekstd-compatible framing.
var writer = new SeekableWriter(new SeekableOptions { FrameSize = 8192 });
writer.Write(chunk);
byte[] file = writer.Finish();
var reader = new SeekableReader(file);
byte[] slice = reader.DecompressRange(offset, length);
```

Blocks that do not compress smaller are stored raw (same rule as upstream
`StoreBlock`); the raw-only `ZarRawCompressor` stays available for
tests and benchmarks.

## Byte-identity target

The encoder, the archive container and the seekable framing are
byte-identical to the frozen references (libzstd 1.5.7, zeekstd): the test
suite proves it against the native tools on thousands of vectors, and
`ZArchiveSharp.Tests/Goldens/` pins native bytes (libzstd one-shots, a
`zarchive.exe` pack, C-library seekable files) so CI holds the line with no
toolchain installed.

Two known boundaries:

- The shipped `zarchive.exe` bundles libzstd 1.5.2, whose level 6 can differ
  from 1.5.7 on multi-transition hetero 64 KiB blocks. Our frames follow the
  frozen 1.5.7: packs are byte-identical to the exe wherever the two libzstd
  versions agree (homogeneous/single-transition blocks, verified), and
  extract interops both ways regardless.
- The reference C seekable library writes plain zstd frames while zeekstd
  (our parity target) sets the frame content-checksum flag when checksums are
  on. Both flavors are valid; our reader decodes both, our writer emits the
  zeekstd flavor.

`ZarchiveCli` deliberately deviates in three places where native behavior is
a bug: an unopenable extract output throws (native writes into the dead
stream), a mid-file input read error fails the pack with `-16` (native packs
a silent truncation), and error-string paths use `/` on every OS.

## What's new in v1.2.0

- **Hardened extraction** — entry names must be single plain components
  (traversal, absolute, drive-qualified and reserved device names are
  rejected), the resolved path is re-validated against the destination root,
  nesting is capped at `ZarPackEngine.MaxExtractDepth` (1024), and files are
  written through scratch files moved into place after the size check.
- **Safer pack walking** — `DirectoryPackSource` never descends directory
  symlinks/junctions; the link stays as an empty directory entry.
- **New decoder cap** — `ZstdDecoderOptions.MaxTotalOutputSize` (1 GiB
  default) bounds concatenated frames cumulatively; `ZstdCompressor.DecompressFrame`
  enforces its cap during decode.
- **New engine helpers** — `ZarPackEngine.MoveIntoPlace`,
  `OutputExistsMessage` and `MaxExtractDepth`; `PackEntries` returns the path
  actually written and takes an optional collision policy.
- **Contract changes** — `SeekableReader` range errors are now
  `ArgumentOutOfRangeException`; `ZArchiveReader.TryOpen(Stream)` disposes a
  failed non-`leaveOpen` stream; `PauseTokenSource` is `IDisposable`.
- **Native-parity fixes** — Windows-1252 byte identity/order for name tables,
  splitter repeat-offset history, stream header rebasing and wrap-safe
  bounds checks.

Full notes: [WhatsNew.md](../WhatsNew.md) · [docs](../docs/README.md).

## Limits

- No zstd dictionary *training*, LDM, legacy frames, or multithreading
  inside one zstd frame (each 64 KiB block is an independent frame, so
  packs/extracts parallelize across blocks, byte-identical).
  Dictionary *use* (`ZstdDictionary`, `--dict`) is supported.
- Decoder caps (configurable via `ZstdDecoderOptions`): 512 MiB window,
  512 MiB frame content, 1 GiB total output across one `Decompress` call.
- Corrupt archives throw documented exceptions (`ZarArchiveOpenException`,
  `ZarInputOpenException`, `ZarEntryCreateException`, `ZstdException`,
  `IOException`); truncations always fail the open. Neither implementation
  verifies archive integrity (data blocks carry no checksums), so flipped
  bytes may decode to different content instead of throwing — same as native.

## Release process

Versions derive from git tags via MinVer; tags must be v-prefixed annotated
tags (`v1.0.0`-style — `MinVerTagPrefix=v` in `Directory.Build.props`;
without it MinVer 8 silently ignores `v*` tags). Every push builds and
tests on Ubuntu/Windows/macOS; `dotnet pack` runs for the library and the
CLI tool (API-validated, symbols + SourceLink), and pushed `v*` tags publish to
NuGet. See `.github/workflows/ci.yml`. Per-release highlights live in
[WhatsNew.md](../WhatsNew.md).

## License

MIT — see [LICENSE](../LICENSE).
