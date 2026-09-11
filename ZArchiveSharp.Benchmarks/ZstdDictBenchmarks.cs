using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class ZstdDictBenchmarks
{
    private const int Files2K = 64;
    private const int Files8K = 32;

    private ZstdCompressor _plain = null!;
    private ZstdCompressor _withDict = null!;
    private ZstdDictionary _dict = null!;
    private byte[][] _small2K = null!;
    private byte[][] _small8K = null!;
    private byte[][] _plain2K = null!;
    private byte[][] _dict2K = null!;
    private byte[][] _plain8K = null!;
    private byte[][] _dict8K = null!;

    /// <summary>
    /// Builds the dictionary, the file corpus and all decode fixtures once per
    /// benchmark process, and logs deterministic per-file sizes for the docs table.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _dict = ZstdDictionary.FromRawPrefix(BenchmarkCorpus.CycleText(8192));
        _plain = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _withDict = new ZstdCompressor(new ZstdCompressionOptions { Level = 6, Dictionary = _dict });
        _small2K = new byte[Files2K][];
        _small8K = new byte[Files8K][];
        for (var i = 0; i < Files2K; i++)
        {
            _small2K[i] = BenchmarkCorpus.CycleTextAt(2048, i * 53);
        }

        for (var i = 0; i < Files8K; i++)
        {
            _small8K[i] = BenchmarkCorpus.CycleTextAt(8192, i * 211);
        }

        _plain2K = CompressAll(_plain, _small2K);
        _dict2K = CompressAll(_withDict, _small2K);
        _plain8K = CompressAll(_plain, _small8K);
        _dict8K = CompressAll(_withDict, _small8K);
        Console.WriteLine(
            $"[fixtures] 2k/file plain={Avg(_plain2K)} dict={Avg(_dict2K)} 8k/file plain={Avg(_plain8K)} dict={Avg(_dict8K)}");
    }

    private static byte[][] CompressAll(ZstdCompressor compressor, byte[][] files)
    {
        var frames = new byte[files.Length][];
        for (var i = 0; i < files.Length; i++)
        {
            frames[i] = compressor.CompressBlock(files[i]);
        }

        return frames;
    }

    private static long Avg(byte[][] frames)
    {
        long total = 0;
        foreach (var frame in frames)
        {
            total += frame.Length;
        }

        return total / frames.Length;
    }

    private static long TotalLength(byte[][] frames)
    {
        long total = 0;
        foreach (var frame in frames)
        {
            total += frame.Length;
        }

        return total;
    }

    /// <summary>Compresses the 2 KiB set with no dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_2k_Plain()
    {
        return TotalLength(CompressAll(_plain, _small2K));
    }

    /// <summary>Compresses the 2 KiB set with the dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_2k_Dict()
    {
        return TotalLength(CompressAll(_withDict, _small2K));
    }

    /// <summary>Compresses the 8 KiB set with no dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_8k_Plain()
    {
        return TotalLength(CompressAll(_plain, _small8K));
    }

    /// <summary>Compresses the 8 KiB set with the dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_8k_Dict()
    {
        return TotalLength(CompressAll(_withDict, _small8K));
    }

    /// <summary>Decodes the plain 2 KiB frames.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_2k_Plain()
    {
        long total = 0;
        foreach (var frame in _plain2K)
        {
            total += ZstdCompressor.DecompressFrame(frame, maxSize: 2048).Length;
        }

        return total;
    }

    /// <summary>Decodes the dictionary 2 KiB frames.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_2k_Dict()
    {
        long total = 0;
        foreach (var frame in _dict2K)
        {
            total += ZstdDecompressor.Decompress(frame, _dict).Length;
        }

        return total;
    }

    /// <summary>Decodes the plain 8 KiB frames.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_8k_Plain()
    {
        long total = 0;
        foreach (var frame in _plain8K)
        {
            total += ZstdCompressor.DecompressFrame(frame, maxSize: 8192).Length;
        }

        return total;
    }

    /// <summary>Decodes the dictionary 8 KiB frames.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_8k_Dict()
    {
        long total = 0;
        foreach (var frame in _dict8K)
        {
            total += ZstdDecompressor.Decompress(frame, _dict).Length;
        }

        return total;
    }
}