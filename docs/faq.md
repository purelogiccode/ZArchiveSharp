# FAQ

Frequently asked questions and troubleshooting.

## General

### What is ZArchiveSharp?

A pure-C# port of the [ZArchive 0.1.2](https://github.com/unknownbrackets/ZArchive) library: directory-tree archives with per-block zstd compression. It also bundles a complete dependency-free RFC 8878 zstd codec, a zeekstd-compatible seekable zstd implementation, and a batch pipeline engine.

### Why "byte-identical" output? Why does it matter?

Archives created by ZArchiveSharp are **bit-for-bit the same** as those created by the original C++ `zarchive.exe` for the same API call sequence. This means:

- Files pack to the exact same size, no regression risk when switching implementations
- Existing tooling, hash checks and distribution workflows keep working
- Any `.zar` from the ecosystem extracts here, and vice versa

The test suite enforces this with thousands of parity vectors and committed golden files.

### What are the runtime requirements?

.NET 8.0, 9.0, or 10.0. No native dependencies, no `unsafe` code on the zstd path, BCL only. The library is trimmable and AOT-compatible.

### Is it production ready?

The port is complete (Steps 1–6 of the port plan), with 3800+ green tests including native-tool parity matrices and committed goldens. Performance is the main known gap (see [Benchmarks](benchmarks.md)).

## Compatibility

### Can zarchive.exe read my archives? Can I read zarchive.exe archives?

Yes, both directions. The suite tests interop both ways, and a native-packed `.zar` is committed as a golden artifact.

### Can standard zstd tools read the standalone frames?

Yes — frames follow RFC 8878 and decode with the official `zstd` CLI. Conversely, ZARSharp decodes anything standard tools produce at levels 1–22 (no dictionaries, no legacy frames).

### Why does my output differ from zarchive.exe in rare cases?

The shipped `zarchive.exe` bundles **libzstd 1.5.2**; ZARSharp targets the frozen **1.5.7** reference. On some multi-transition heterogeneous 64 KiB blocks, level 6 differs between those libzstd versions (a 1.5.2-vs-1.5.7 upstream change, not a bug). Homogeneous blocks are identical; extract interops both ways regardless.

### Can I read seekable files made by zeekstd / the C reference?

Yes. The two write different flavors (zeekstd sets the frame content-checksum flag, the C library writes plain frames) — both are valid and both decode here. Our writer emits the zeekstd flavor by default.

## Usage

### Which compression level should I pick?

| Goal | Level |
|------|-------|
| Match `zarchive.exe` default | 6 (the default) |
| Maximum speed | 1–3 |
| Balanced | 6–9 |
| Small archives, time budget exists | 12–15 |
| Archival, size over everything | 19–22 |

See [Zstd Compression](zstd-compression.md#level-selection-guidelines).

### How do I make archives reproducible?

`ZArchiveTool.Pack` sorts entries ordinally by default (`deterministicOrder: true`) — same input tree yields byte-identical output. Do not combine with `Checksum`/custom compressors if you need stable bytes across machines.

### How do I store files without compression?

```csharp
ZArchiveTool.Pack(dir, out, compressor: new ZarRawCompressor());
// or CLI: zar --no-compress <dir>
```

### How do I get native-libzstd-class speed?

Plug a block compressor into `IZarBlockCompressor` (e.g. a native interop adapter). Note byte parity with `zarchive.exe` is only guaranteed with the built-in `ZstdCompressor` — custom compressors are opt-in for a reason.

### Does it support multi-threaded compression?

Inside one frame, no (like upstream). Across items, yes: the pipeline packs batches in parallel (`MaxDegreeOfParallelism`). Each 64 KiB block is an independent frame, so batch parallelism scales cleanly.

### Are dictionaries supported?

No — zstd dictionaries are out of scope. Negative levels are also rejected (locked by tests).

## Errors

### `ZArchiveReader.TryOpen` returned null — what do I do?

`TryOpen` never throws; `null` means the file failed validation (bad magic/version, hash mismatch, truncated, malformed tables). Common causes:

- The file is not a `.zar` at all (check for a download wrapper, e.g. HTML)
- Truncated transfer — re-download; truncations always fail the open
- Newer format revision (0.1.2 only is supported)

### Flipping bytes in an archive doesn't always throw — why?

Data blocks carry no per-block checksums (same as native). The footer SHA-256 catches most corruption, but a flip inside a data block may simply decode to different bytes. If integrity matters, hash your files independently.

### My pack fails with exit code -16 / an IOException

`-16` (`PackOutputFailed`) is an output I/O error — disk full, permission denied, or the destination vanished mid-run. ZARSharp fails the pack here where native `zarchive.exe` would silently pack a truncated file (a documented intentional deviation).

### Extraction fails with -12 (ExtractionFailed)

The archive is corrupt or the I/O failed mid-extract. Extraction lines printed before the failure show how far it got (preorder, including directories).

## Project

### What is the license?


### How do I contribute?

Open an issue or PR on the [issue tracker](https://github.com/purelogiccode/ZArchiveSharp/issues). If you touch compression logic, keep byte parity: the parity tests and goldens in `ZARSharp.Tests/Goldens/` must stay green, and CI holds the line with no native toolchain installed.

### How are versions managed?

Versions derive from git tags via MinVer (`v1.0.0`-style annotated tags; `MinVerTagPrefix=v`). Every push builds and tests on Ubuntu/Windows/macOS; tag pushes publish to NuGet.
