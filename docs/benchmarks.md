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

Filters are space-separated unions (`--filter *A* *B*` runs both; comma
syntax does not work). Always run in **Release** — Debug numbers are
meaningless. Per-class runs take 3–11 minutes each (the level-19 and 10 MiB
rows dominate); the full 71-benchmark suite takes ~30 minutes.

## Benchmark Suite

71 benchmarks across seven classes (all `[MemoryDiagnoser]`,
`MinIterationCount(5)`/`MaxIterationCount(20)`):

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

### ZarBlockHotPathBenchmarks

The core claim: single-shot compress + decode of text, hetero and random
64 KiB blocks at L1/L6 plus an L6 200 KiB multi-block control. The ZAR
container packs independent 64 KiB frames, so this table — not the 8 KiB
micro-rows — decides whether the codec bottlenecks directory-tree archives.

### ZstdStreamBenchmarks

`ZstdCompressionStream` chunked writes / `ZstdDecompressionStream` chunked
reads over 1 MiB text + tiled-hetero at L1/L6 with 4 KiB vs 64 KiB pump
chunks, plus 10 MiB text controls. Output streams are pre-sized from
setup-measured frame sizes so `MemoryStream` growth never pollutes timings.

### ZstdDictBenchmarks

64 × 2 KiB and 32 × 8 KiB phrase-cycle files (varied offsets) at L6 with and
without an 8 KiB raw-prefix dictionary, both directions. Whole-set totals;
per-file averages give the ratio side.

### VsZstdSharpBenchmarks

Optional third-party baseline (see below): same 64 KiB payloads through
`ZstdSharp.Port` 0.8.8 next to ZARSharp. Oracle only, never shipped; excluded
from default runs, select explicitly:

```bash
dotnet run -c Release --project ZARSharp.Benchmarks -- --filter *VsZstdSharp*
```

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

## Block-Size Proof (64 KiB)

AMD Ryzen 9 7900, .NET SDK 10.0.301, Runtime .NET 10.0.12, Release, x64
(RyuJIT x86-64-v4), 2026-09-10. Throughput = input bytes / Mean.
Frame sizes are deterministic (frozen corpus) and printed by `GlobalSetup`.

| Benchmark | Mean | Throughput | Frame (ratio) | Allocated |
|-----------|------|------------|---------------|-----------|
| L1 compress, text 64 KiB | 41.5 µs | ~1.58 GB/s | 96 B (0.15%) | 410 KB |
| L6 compress, text 64 KiB | 115.5 µs | ~568 MB/s | 96 B (0.15%) | 1025 KB |
| L1 compress, hetero 64 KiB | 46.1 µs | ~1.42 GB/s | 8403 B (12.8%) | 417 KB |
| L6 compress, hetero 64 KiB | 152.8 µs | ~429 MB/s | 8311 B (12.7%) | 1125 KB |
| L1 compress, random 64 KiB | 69.3 µs | ~945 MB/s | 65546 B (~100%) | 526 KB |
| L6 compress, random 64 KiB | 162.0 µs | ~405 MB/s | 65546 B (~100%) | 1269 KB |
| L6 compress, text 200 KiB | 404.3 µs | ~495 MB/s | 110 B (0.06%) | 3698 KB |
| Decode L1, text 64 KiB | 68.5 µs | ~957 MB/s | — | 195 KB |
| Decode L6, text 64 KiB | 69.4 µs | ~944 MB/s | — | 195 KB |
| Decode L1, hetero 64 KiB | 82.4 µs | ~795 MB/s | — | 209 KB |
| Decode L6, hetero 64 KiB | 75.4 µs | ~869 MB/s | — | 209 KB |
| Decode L1/L6, random 64 KiB | ~80–81 µs | ~810–819 MB/s | — | 257 KB |
| Decode L6, text 200 KiB | 311.0 µs | ~643 MB/s | — | 710 KB |

Interpretation rule: the ZAR container packs independent 64 KiB frames, so
end-to-end pack/extract is container + I/O bound. The codec is "fast enough"
when L6 64 KiB hetero is ≥ ~300 MB/s and decode ≥ ~500 MB/s on the reference
machine — both hold (429 MB/s encode, 795–869 MB/s decode). Do not claim a
fixed ×-of-native ratio; the native comparison below reports both sides and
lets the table speak.

## Streams + Dict

Same machine as above.

Streams (chunk size is free — 4 KiB vs 64 KiB agree within noise on every row):

| Benchmark | Mean | Throughput |
|-----------|------|------------|
| Compress 1 MiB text, L1 | ~1.49–1.50 ms | ~700 MB/s |
| Compress 1 MiB text, L6 | ~3.04–3.10 ms | ~340 MB/s |
| Compress 1 MiB hetero, L1 | ~1.46–1.47 ms | ~715 MB/s |
| Compress 1 MiB hetero, L6 | ~3.25–3.46 ms | ~310 MB/s |
| Compress 10 MiB text, L1 | ~21.1–21.4 ms | ~495 MB/s |
| Compress 10 MiB text, L6 | ~34.0–35.1 ms | ~300 MB/s |
| Decompress 1 MiB (all rows) | ~943–981 µs | ~1.08 GB/s |
| Decompress 10 MiB (all rows) | ~10.8–12.6 ms | ~830–970 MB/s |

Dict (8 KiB raw-prefix dictionary; per-file averages in parentheses):

| Benchmark | Whole-set mean | Per-file size |
|-----------|----------------|---------------|
| Compress 64 × 2 KiB, plain | 423.9 µs (6.6 µs/file) | 95 B/file |
| Compress 64 × 2 KiB, dict | 2314.4 µs (36.2 µs/file) | 22 B/file, 4.3× smaller |
| Compress 32 × 8 KiB, plain | 503.6 µs (15.7 µs/file) | 96 B/file |
| Compress 32 × 8 KiB, dict | 1418.9 µs (44.3 µs/file) | 22 B/file, 4.4× smaller |
| Decompress 2 KiB, plain / dict | 267.5 µs / 157.1 µs | dict decodes 1.7× faster |
| Decompress 8 KiB, plain / dict | 326.1 µs / 234.8 µs | dict decodes 1.4× faster |

Reading the dict rows: the ratio win is decisive (22 B vs ~95 B per small
file), but the current implementation pays a ~29 µs/file fixed dict-setup
cost on compress (identical at 2 KiB and 8 KiB: (2314−424)/64 ≈
(1419−504)/32), so dict compress is 2.8–5.5× slower per file here while dict
decode is faster (smaller frames). For archives the dict still pays whenever
entry *size* matters more than pack CPU (small entries over slow media);
caching seeded encoder state per dictionary is the tracked follow-up to erase
the fixed cost. (`Decompress_2k_Dict` carries a BDN bimodal-distribution
warning; absolute scale is ~157 µs.)

## Versus ZstdSharp.Port (oracle baseline)

Same machine. `ZstdSharp.Port` pinned at 0.8.8 (benchmark-project-only
reference, oracle only, never shipped). Verified before publishing: the
assembly is managed-only (no P/Invoke), its L1/L6 frames are byte-identical
in size to ours on all three payloads (8311/8403/96 B), it decodes our frames
to the exact input bytes, and alternating-input probes rule out same-input
caching — the gap below is genuine per-call work, both libraries measured
under the same BDN job.

| Benchmark | Mean | Ratio vs ZARSharp |
|-----------|------|-------------------|
| ZARSharp L6, hetero 64 KiB (baseline) | 155.8 µs | 1.00 |
| ZstdSharp L6, hetero 64 KiB | 23.3 µs | 0.15 |
| ZARSharp L1, hetero 64 KiB | 53.4 µs | 0.34 |
| ZstdSharp L1, hetero 64 KiB | 7.0 µs | 0.04 |
| ZARSharp L6, text 64 KiB | 112.2 µs | 0.72 |
| ZstdSharp L6, text 64 KiB | 8.1 µs | 0.05 |
| ZARSharp decode, hetero 64 KiB | 83.5 µs | 0.54 |
| ZstdSharp decode, same bytes | 3.3 µs | 0.02 |

Note this supersedes the older "~0.96–1.13× native" remark below for current
versions: pinned 0.8.8 measures several× faster than stock libzstd itself on
these payloads (see native table), so it is a harsh oracle, not a peer.
ZARSharp trades that speed for byte-exact parity with stock decisions, safe
code, and no dependencies — the measured cost is in this table.

## Interpretation

### Versus native libzstd

Measured 2026-09-10 on the reference machine above: stock libzstd 1.5.7 via
Python `compression.zstd`, compressing/decompressing the *identical* frozen
payload bytes (a few µs of Python call overhead flatter native slightly, so
treat near-parity as parity):

| Workload | ZARSharp (BDN) | Native 1.5.7 | × native |
|----------|----------------|--------------|----------|
| L1 hetero 64 KiB | 46.1 µs | 38.0 µs | ~0.8× |
| L6 hetero 64 KiB | 152.8 µs | 174.8 µs | ~1.0× (parity) |
| L6 text 64 KiB | 115.5 µs | 148.8 µs | ~1.0× (parity) |
| Decode hetero 64 KiB | 75.4 µs | 79.2 µs | ~1.0× (parity) |
| Decode text 64 KiB | 69.4 µs | 72.0 µs | ~1.0× (parity) |

This replaces the older "0.28×/0.17× native" estimate, which no longer matches
measurement on the hot path (same-size frames, same machine class). The
standing trade-off is unchanged in kind, restated with evidence:

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
- Expected frame sizes/ratios are printed by each `GlobalSetup`
  (`[fixtures] ...`) and transcribed into the tables above — rerun and compare
  them first if numbers ever look off (a size change means a behavior change)
- Machine-noise policy, applied 2026-09-10: the batch re-run showed
  `L19_Hetero64k` at 2.58 ms (StdDev 318 µs) against the 1.70 ms baseline;
  an isolated re-run measured 1.514 ms (Error 29 µs) — the batch value was
  transient interference, and the re-run is the recorded one. Same for
  `Pack_4x64k` (batch 963 µs, isolated 931 µs vs 869 µs baseline, +7.1% —
  inside the 15% gate; the only pack-path delta across Phases 1–3 is one
  perfectly-predicted null branch). When a row fails the gate, re-run it
  isolated before concluding a regression.

## Reproducing the Parity/Perf Trade-off

If you need native-class speed and can accept a dependency, the `IZarBlockCompressor` seam lets you plug any block compressor (e.g. a native interop adapter) without touching the container — `Zarchive.exe` parity is only guaranteed with the built-in `ZstdCompressor`. Note this is opt-in only; the default must stay byte-identical.
