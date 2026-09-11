# ZArchiveSharp Documentation

Welcome to the ZArchiveSharp documentation. This guide covers everything you need to know about using ZArchiveSharp for archive creation, zstd compression, and batch processing.

## Table of Contents

### Getting Started
- **[Getting Started](getting-started.md)** — Installation, prerequisites, and your first archive
- **[CLI Reference](cli-reference.md)** — Command-line tool usage and options

### Core Concepts
- **[ZAR Format](zar-format.md)** — Archive format specification, structure, and design decisions
- **[Zstd Compression](zstd-compression.md)** — Compression levels, strategies, and tuning guide
- **[Seekable Format](seekable-format.md)** — Seekable zstd framing (Foot + Head tables)

### API Documentation
- **[API Reference](api-reference.md)** — Complete library API for all public types and methods
- **[Pipeline](pipeline.md)** — Batch operations, progress reporting, and collision handling

### Advanced Topics
- **[Benchmarks](benchmarks.md)** — Performance characteristics and optimization tips
- **[FAQ](faq.md)** — Frequently asked questions and troubleshooting

---

## Overview

ZArchiveSharp is a pure-C# port of the [ZArchive 0.1.2](https://github.com/unknownbrackets/ZArchive) library. It provides:

1. **Archive format** — Directory-tree archives with per-block zstd compression (64 KiB blocks)
2. **zstd codec** — Complete RFC 8878 encoder and decoder (levels 1–22, all 9 strategies)
3. **Seekable format** — zeekstd-compatible seekable zstd framing
4. **Pipeline** — Batch pack/extract with progress, pause, cancellation, and collision policies
5. **CLI tool** — `zar` command matching `zarchive.exe` behavior

### Key Properties

| Property | Value |
|----------|-------|
| **Target frameworks** | `net8.0`, `net9.0`, `net10.0` |
| **Runtime dependencies** | None (BCL only) |
| **Unsafe code** | None in the zstd path |
| **Trimmable** | Yes |
| **AOT compatible** | Yes |
| **License** | MIT |

### Byte-Identity Guarantee

ZArchiveSharp produces **byte-identical output** to:
- The original C++ `zarchive.exe` (ZArchive 0.1.2)
- libzstd 1.5.7 (frozen reference)
- zeekstd (seekable format)

This is verified by over 3800 tests including parity validation against native tools.

---

## Quick Links

| Topic | Description |
|-------|-------------|
| [Installation](getting-started.md#installation) | How to install ZArchiveSharp |
| [Pack a Directory](getting-started.md#packing-a-directory) | Create your first archive |
| [Extract an Archive](getting-started.md#extracting-an-archive) | Extract archive contents |
| [CLI Usage](cli-reference.md#usage) | Command-line tool reference |
| [Compression Levels](zstd-compression.md#compression-levels) | Choosing the right level |
| [Pipeline API](pipeline.md#basic-usage) | Batch operations |
| [Error Handling](api-reference.md#error-handling) | Exception types and handling |
