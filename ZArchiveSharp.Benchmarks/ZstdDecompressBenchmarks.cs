using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class ZstdDecompressBenchmarks
{
    private byte[] _l1Text = null!;
    private byte[] _l6Text = null!;
    private byte[] _l19Text = null!;
    private byte[] _l6Hetero = null!;
    private byte[] _l6Text200K = null!;

    /// <summary>
    /// Pre-compresses the decode fixtures once per benchmark process.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        var text8K = BenchmarkCorpus.CycleText(8192);
        _l1Text = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1)).CompressBlock(text8K);
        _l6Text = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(text8K);
        _l19Text = new ZstdCompressor(ZstdCompressionOptions.FromLevel(19)).CompressBlock(text8K);
        _l6Hetero = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(BenchmarkCorpus.Hetero64());
        _l6Text200K =
            new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(BenchmarkCorpus.CycleText(200000));
    }

    /// <summary>Decodes the level-1 text frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] L1_Text8k()
    {
        return ZstdCompressor.DecompressFrame(_l1Text, maxSize: 8192);
    }

    /// <summary>Decodes the level-6 text frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] L6_Text8k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text, maxSize: 8192);
    }

    /// <summary>Decodes the level-19 text frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] L19_Text8k()
    {
        return ZstdCompressor.DecompressFrame(_l19Text, maxSize: 8192);
    }

    /// <summary>Decodes the level-6 hetero 64 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] L6_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_l6Hetero, maxSize: 65536);
    }

    /// <summary>Decodes the level-6 multi-block 200 KiB frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] L6_Text200k()
    {
        return ZstdCompressor.DecompressFrame(_l6Text200K, maxSize: 200000);
    }
}