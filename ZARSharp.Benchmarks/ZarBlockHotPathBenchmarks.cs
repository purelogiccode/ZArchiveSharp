using BenchmarkDotNet.Attributes;
using ZARSharp.Zstd;

namespace ZARSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// The core 64 KiB claim: single-shot compress + decode of text, hetero and
/// random 64 KiB blocks at L1 and L6 (the ZAR container's hot path, default 6),
/// plus an L6 200 KiB multi-block control. The ZAR container packs independent
/// 64 KiB frames, so this table — not the 8 KiB micro-rows — is the number that
/// decides whether the codec bottlenecks directory-tree archives. Expected
/// frame sizes/ratios are printed by <c>GlobalSetup</c> (deterministic corpus,
/// so they are stable across runs) and transcribed into
/// <c>docs/benchmarks.md</c>.
/// </summary>
public class ZarBlockHotPathBenchmarks
{
    private ZstdCompressor _l1 = null!;
    private ZstdCompressor _l6 = null!;
    private byte[] _text64k = null!;
    private byte[] _hetero64k = null!;
    private byte[] _random64k = null!;
    private byte[] _text200k = null!;
    private byte[] _l1Text64k = null!;
    private byte[] _l6Text64k = null!;
    private byte[] _l1Hetero64k = null!;
    private byte[] _l6Hetero64k = null!;
    private byte[] _l1Random64k = null!;
    private byte[] _l6Random64k = null!;
    private byte[] _l6Text200k = null!;

    /// <summary>
    /// Builds the compressors, frozen payloads and decode fixtures once per
    /// benchmark process, and logs the deterministic frame sizes for the docs table.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _l1 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1));
        _l6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _text64k = BenchmarkCorpus.CycleText(65536);
        _hetero64k = BenchmarkCorpus.Hetero64();
        _random64k = BenchmarkCorpus.Random(65536);
        _text200k = BenchmarkCorpus.CycleText(200000);
        _l1Text64k = _l1.CompressBlock(_text64k);
        _l6Text64k = _l6.CompressBlock(_text64k);
        _l1Hetero64k = _l1.CompressBlock(_hetero64k);
        _l6Hetero64k = _l6.CompressBlock(_hetero64k);
        _l1Random64k = _l1.CompressBlock(_random64k);
        _l6Random64k = _l6.CompressBlock(_random64k);
        _l6Text200k = _l6.CompressBlock(_text200k);
        Console.WriteLine($"[fixtures] L1 text64k={_l1Text64k.Length} hetero64k={_l1Hetero64k.Length} random64k={_l1Random64k.Length}");
        Console.WriteLine($"[fixtures] L6 text64k={_l6Text64k.Length} hetero64k={_l6Hetero64k.Length} random64k={_l6Random64k.Length} text200k={_l6Text200k.Length}");
    }

    /// <summary>Level 1 over 64 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text64k()
    {
        return _l1.CompressBlock(_text64k);
    }

    /// <summary>Level 6 over 64 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text64k()
    {
        return _l6.CompressBlock(_text64k);
    }

    /// <summary>Level 1 over the hetero 64 KiB block.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Hetero64k()
    {
        return _l1.CompressBlock(_hetero64k);
    }

    /// <summary>Level 6 over the hetero 64 KiB block (the container's typical block).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Hetero64k()
    {
        return _l6.CompressBlock(_hetero64k);
    }

    /// <summary>Level 1 over 64 KiB of random bytes (raw-fallback path at block scale).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Random64k()
    {
        return _l1.CompressBlock(_random64k);
    }

    /// <summary>Level 6 over 64 KiB of random bytes (raw-fallback path at block scale).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Random64k()
    {
        return _l6.CompressBlock(_random64k);
    }

    /// <summary>Level 6 over 200 KiB of text (multi-block control).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text200k()
    {
        return _l6.CompressBlock(_text200k);
    }

    /// <summary>Decodes the level-1 text 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Text64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Text64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 text 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Text64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-1 hetero 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Hetero64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 hetero 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Hetero64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-1 random 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Random64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Random64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 random 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Random64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Random64k, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 multi-block 200 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Text200k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text200k, maxSize: 200000);
    }
}
