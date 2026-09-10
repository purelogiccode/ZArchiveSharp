# Benchmarks

ZARSharp ships a [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) suite (`ZARSharp.Benchmarks`) covering the zstd codec and the archive container. This page records the current baseline and explains how to run and interpret the benchmarks.

## Running

```bash
dotnet run -c Release -f net10.0 --project ZARSharp.Benchmarks
```

BenchmarkDotNet prompts for a benchmark filter interactively; pass `--filter` to select directly:

```bash
# Compress benchmarks only
dotnet run -c Release --project ZARSharp.Benchmarks -- --filter *ZstdCompress*

# Everything
dotnet run -c Release --project ZARSharp.Benchmarks -- --filter *
```

Always run in **Release** — Debug numbers are meaningless. A full run takes several minutes (the btultra2/level-19 rows dominate).

## Benchmark Suite

17 benchmarks across three classes:

### ZstdCompressBenchmarks

Single-shot `ZstdCompressor.CompressBlock` frames at levels **1 / 6 / 19** over four payload kinds:

| Payload | Content | Purpose |
|---------|---------|---------|
| `Text8k` | Phrase-cycle text | Compressible fast path |
| `Random8k` | Seeded random bytes | Raw-fallback path |
| `Hetero64k` | 16 KiB text + 8 KiB random + 8 KiB text, zero-padded to 64 KiB | The ZAR container's real-world block shape |
| `Text200k` | 200 KiB phrase-cycle text | Multi-block (128 KiB) path |

Levels 1 and 6 are the ZAR container's hot path (default 6); level 19 pins the btultra2 binary-tree finder cost.

### ZstdDecompressBenchmarks

Decoding the same payload kinds — five decode benchmarks.

### ZarArchiveBenchmarks

End-to-end container operations: pack and extract of a 4 × 64 KiB file tree.

## Baseline Results

net10.0, Release, x64 (indicative — rerun on your hardware):

| Benchmark | Mean | Throughput |
|-----------|------|-----------|
| L1 compress, hetero 64 KiB | 49 µs | ~1.3 GB/s |
| L6 compress, hetero 64 KiB | 145 µs | ~450 MB/s |
| L6 compress, 200 KiB text | 427 µs | ~470 MB/s |
| L19 compress, hetero 64 KiB | 1.70 ms | ~38 MB/s |
| Decode (all payloads) | — | ~860 MB/s |
| Pack 4 × 64 KiB tree | 869 µs | — |
| Extract 4 × 64 KiB tree | 310 µs | — |

## Interpretation

### Versus native libzstd

Pure C# runs at roughly **0.28× native at level 6** (and ~0.17× at level 1). For reference, the ZstdSharp port (with `unsafe` + IL-weaving) reaches ~0.96–1.13× native. ZARSharp deliberately chose:

- **Byte-exact parity** over speed — every strategy, entropy decision and splitter boundary must match libzstd 1.5.7 byte-for-byte
- **Safe code** — no `unsafe`, no P/Invoke, BCL only, AOT-compatible

The known optimization lever: the compress path allocates ~1 bound-size buffer per frame (e.g. 3.7 MB for 200 KiB input). **ArrayPool rental** is the obvious next step. Span-specializing the hot match-finder loop is the other tracked follow-up.

### What to watch when profiling

- The match finders (`ZstdFast`, `ZstdDoubleFast`, `ZstdLazyEngine`) dominate levels 1–9
- The binary-tree optimal parser (`ZstdOpt` + `ZstdBinaryTree`) dominates 13–22
- Entropy coding (`ZstdFseEncoder`, `ZstdHuffmanEncoder`) is the second hot spot at every level
- Decode is a single `ZstdDecompressor` path — already near the safe-C# ceiling

## Methodology Notes

- Payloads are **deterministic and frozen** (`BenchmarkCorpus`): fixed seeds only, never `HashCode.Combine` (process-randomized — would silently redraw vectors per run)
- Buffers are built once in `[GlobalSetup]` and never mutated; the compressor/reader never modify inputs
- `[MemoryDiagnoser]` is on, so allocation columns are trustworthy
- Iterations are bounded (`MinIterationCount(5)`, `MaxIterationCount(20)`) to keep the level-19 rows tolerable

## Reproducing the Parity/Perf Trade-off

If you need native-class speed and can accept a dependency, the `IZarBlockCompressor` seam lets you plug any block compressor (e.g. a native interop adapter) without touching the container — `Zarchive.exe` parity is only guaranteed with the built-in `ZstdCompressor`. Note this is opt-in only; the default must stay byte-identical.
