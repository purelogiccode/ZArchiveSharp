using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class ZstdCompressBenchmarks
{
    private ZstdCompressor _l1 = null!;
    private ZstdCompressor _l6 = null!;
    private ZstdCompressor _l19 = null!;
    private byte[] _text8K = null!;
    private byte[] _random8K = null!;
    private byte[] _hetero64K = null!;
    private byte[] _text200K = null!;

    /// <summary>
    /// Builds the compressors and frozen payloads once per benchmark process.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _l1 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1));
        _l6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _l19 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(19));
        _text8K = BenchmarkCorpus.CycleText(8192);
        _random8K = BenchmarkCorpus.Random(8192);
        _hetero64K = BenchmarkCorpus.Hetero64();
        _text200K = BenchmarkCorpus.CycleText(200000);
    }

    /// <summary>Level 1 over 8 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text8k()
    {
        return _l1.CompressBlock(_text8K);
    }

    /// <summary>Level 1 over 8 KiB of random bytes (exercises the raw-fallback path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Random8k()
    {
        return _l1.CompressBlock(_random8K);
    }

    /// <summary>Level 1 over the hetero 64 KiB block (single compressed block).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Hetero64k()
    {
        return _l1.CompressBlock(_hetero64K);
    }

    /// <summary>Level 1 over 200 KiB of text (multi-block frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text200k()
    {
        return _l1.CompressBlock(_text200K);
    }

    /// <summary>Level 6 over 8 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text8k()
    {
        return _l6.CompressBlock(_text8K);
    }

    /// <summary>Level 6 over 8 KiB of random bytes (exercises the raw-fallback path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Random8k()
    {
        return _l6.CompressBlock(_random8K);
    }

    /// <summary>Level 6 over the hetero 64 KiB block (the ZAR container's typical hot path).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Hetero64k()
    {
        return _l6.CompressBlock(_hetero64K);
    }

    /// <summary>Level 6 over 200 KiB of text (multi-block frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text200k()
    {
        return _l6.CompressBlock(_text200K);
    }

    /// <summary>Level 19 over 8 KiB of text (btultra2 path on a small frame).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L19_Text8k()
    {
        return _l19.CompressBlock(_text8K);
    }

    /// <summary>Level 19 over the hetero 64 KiB block (btultra2 binary-tree finder cost).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L19_Hetero64k()
    {
        return _l19.CompressBlock(_hetero64K);
    }
}