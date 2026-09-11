using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Dictionary payoff for small files: 64 × 2 KiB and 32 × 8 KiB phrase-cycle
/// files (varied offsets, fixed seeds) compressed at L6 with and without an
/// 8 KiB raw-prefix dictionary, plus both decode directions. Each benchmark
/// covers the whole file set and returns the total bytes so per-file averages
/// (printed by <c>GlobalSetup</c>, deterministic corpus) give the ratio side
/// of the "dict is worth it" claim for archive entries.
/// </summary>
public class ZstdDictBenchmarks
{
    private const int Files2k = 64;
    private const int Files8k = 32;

    private ZstdCompressor _plain = null!;
    private ZstdCompressor _withDict = null!;
    private ZstdDictionary _dict = null!;
    private byte[][] _small2k = null!;
    private byte[][] _small8k = null!;
    private byte[][] _plain2k = null!;
    private byte[][] _dict2k = null!;
    private byte[][] _plain8k = null!;
    private byte[][] _dict8k = null!;

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
        _small2k = new byte[Files2k][];
        _small8k = new byte[Files8k][];
        for (var i = 0; i < Files2k; i++)
        {
            _small2k[i] = BenchmarkCorpus.CycleTextAt(2048, i * 53);
        }

        for (var i = 0; i < Files8k; i++)
        {
            _small8k[i] = BenchmarkCorpus.CycleTextAt(8192, i * 211);
        }

        _plain2k = CompressAll(_plain, _small2k);
        _dict2k = CompressAll(_withDict, _small2k);
        _plain8k = CompressAll(_plain, _small8k);
        _dict8k = CompressAll(_withDict, _small8k);
        Console.WriteLine($"[fixtures] 2k/file plain={Avg(_plain2k)} dict={Avg(_dict2k)} 8k/file plain={Avg(_plain8k)} dict={Avg(_dict8k)}");
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
        return TotalLength(CompressAll(_plain, _small2k));
    }

    /// <summary>Compresses the 2 KiB set with the dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_2k_Dict()
    {
        return TotalLength(CompressAll(_withDict, _small2k));
    }

    /// <summary>Compresses the 8 KiB set with no dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_8k_Plain()
    {
        return TotalLength(CompressAll(_plain, _small8k));
    }

    /// <summary>Compresses the 8 KiB set with the dictionary.</summary>
    /// <returns>The total compressed bytes.</returns>
    [Benchmark]
    public long Compress_8k_Dict()
    {
        return TotalLength(CompressAll(_withDict, _small8k));
    }

    /// <summary>Decodes the plain 2 KiB frames.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_2k_Plain()
    {
        long total = 0;
        foreach (var frame in _plain2k)
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
        foreach (var frame in _dict2k)
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
        foreach (var frame in _plain8k)
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
        foreach (var frame in _dict8k)
        {
            total += ZstdDecompressor.Decompress(frame, _dict).Length;
        }

        return total;
    }
}
