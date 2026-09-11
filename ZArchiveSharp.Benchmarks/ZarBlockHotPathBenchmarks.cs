using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class ZarBlockHotPathBenchmarks
{
    private ZstdCompressor _l1 = null!;
    private ZstdCompressor _l6 = null!;
    private byte[] _text64K = null!;
    private byte[] _hetero64K = null!;
    private byte[] _random64K = null!;
    private byte[] _text200K = null!;
    private byte[] _l1Text64K = null!;
    private byte[] _l6Text64K = null!;
    private byte[] _l1Hetero64K = null!;
    private byte[] _l6Hetero64K = null!;
    private byte[] _l1Random64K = null!;
    private byte[] _l6Random64K = null!;
    private byte[] _l6Text200K = null!;

    /// <summary>
    /// Builds the compressors, frozen payloads and decode fixtures once per
    /// benchmark process, and logs the deterministic frame sizes for the docs table.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _l1 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1));
        _l6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _text64K = BenchmarkCorpus.CycleText(65536);
        _hetero64K = BenchmarkCorpus.Hetero64();
        _random64K = BenchmarkCorpus.Random(65536);
        _text200K = BenchmarkCorpus.CycleText(200000);
        _l1Text64K = _l1.CompressBlock(_text64K);
        _l6Text64K = _l6.CompressBlock(_text64K);
        _l1Hetero64K = _l1.CompressBlock(_hetero64K);
        _l6Hetero64K = _l6.CompressBlock(_hetero64K);
        _l1Random64K = _l1.CompressBlock(_random64K);
        _l6Random64K = _l6.CompressBlock(_random64K);
        _l6Text200K = _l6.CompressBlock(_text200K);
        Console.WriteLine(
            $"[fixtures] L1 text64k={_l1Text64K.Length} hetero64k={_l1Hetero64K.Length} random64k={_l1Random64K.Length}");
        Console.WriteLine(
            $"[fixtures] L6 text64k={_l6Text64K.Length} hetero64k={_l6Hetero64K.Length} random64k={_l6Random64K.Length} text200k={_l6Text200K.Length}");
    }

    /// <summary>Level 1 over 64 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Text64k()
    {
        return _l1.CompressBlock(_text64K);
    }

    /// <summary>Level 6 over 64 KiB of phrase-cycle text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text64k()
    {
        return _l6.CompressBlock(_text64K);
    }

    /// <summary>Level 1 over the hetero 64 KiB block.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Hetero64k()
    {
        return _l1.CompressBlock(_hetero64K);
    }

    /// <summary>Level 6 over the hetero 64 KiB block (the container's typical block).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Hetero64k()
    {
        return _l6.CompressBlock(_hetero64K);
    }

    /// <summary>Level 1 over 64 KiB of random bytes (raw-fallback path at block scale).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L1_Random64k()
    {
        return _l1.CompressBlock(_random64K);
    }

    /// <summary>Level 6 over 64 KiB of random bytes (raw-fallback path at block scale).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Random64k()
    {
        return _l6.CompressBlock(_random64K);
    }

    /// <summary>Level 6 over 200 KiB of text (multi-block control).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] L6_Text200k()
    {
        return _l6.CompressBlock(_text200K);
    }

    /// <summary>Decodes the level-1 text 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Text64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Text64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 text 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Text64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-1 hetero 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Hetero64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 hetero 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Hetero64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-1 random 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L1_Random64k()
    {
        return ZstdCompressor.DecompressFrame(_l1Random64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 random 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Random64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Random64K, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 multi-block 200 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Decode_L6_Text200k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text200K, maxSize: 200000);
    }
}