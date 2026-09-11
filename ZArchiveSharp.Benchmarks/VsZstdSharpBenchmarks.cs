using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;
using ZstdSharp;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
/// <summary>
/// Optional third-party baseline: the same 64 KiB payloads through
/// <c>ZstdSharp.Port</c> (a C# zstd port using <c>unsafe</c>) next to ZArchiveSharp.
/// Decode rows run both libraries over the <em>same</em> ZArchiveSharp-produced
/// bytes (byte-identical to stock libzstd frames), so they compare directly.
/// Oracle only, never shipped: needs the extra package restore (pinned to the
/// cached 0.8.8 so it works offline) and is excluded from default runs — use
/// <c>--filter *VsZstdSharp*</c> explicitly.
/// </summary>
public class VsZstdSharpBenchmarks
{
    private ZstdCompressor _zarL1 = null!;
    private ZstdCompressor _zarL6 = null!;
    private Compressor _sharpL1 = null!;
    private Compressor _sharpL6 = null!;
    private Decompressor _sharpDecompressor = null!;
    private byte[] _hetero64k = null!;
    private byte[] _text64k = null!;
    private byte[] _zarL6Hetero = null!;

    /// <summary>
    /// Builds both libraries' compressors and the shared fixtures once per
    /// benchmark process.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _zarL1 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(1));
        _zarL6 = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6));
        _sharpL1 = new Compressor(1);
        _sharpL6 = new Compressor(6);
        _sharpDecompressor = new Decompressor();
        _hetero64k = BenchmarkCorpus.Hetero64();
        _text64k = BenchmarkCorpus.CycleText(65536);
        _zarL6Hetero = _zarL6.CompressBlock(_hetero64k);
    }

    /// <summary>Releases the third-party compressors, if they hold native state.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        if (_sharpL1 is IDisposable l1)
        {
            l1.Dispose();
        }

        if (_sharpL6 is IDisposable l6)
        {
            l6.Dispose();
        }

        if (_sharpDecompressor is IDisposable dec)
        {
            dec.Dispose();
        }
    }

    /// <summary>ZArchiveSharp level 6 over the hetero 64 KiB block (ratio baseline).</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark(Baseline = true)]
    public byte[] Zar_L6_Hetero64k()
    {
        return _zarL6.CompressBlock(_hetero64k);
    }

    /// <summary>ZstdSharp level 6 over the same hetero bytes.</summary>
    /// <returns>The compressed length.</returns>
    [Benchmark]
    public int Sharp_L6_Hetero64k()
    {
        return _sharpL6.Wrap(_hetero64k).Length;
    }

    /// <summary>ZArchiveSharp level 1 over the hetero 64 KiB block.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] Zar_L1_Hetero64k()
    {
        return _zarL1.CompressBlock(_hetero64k);
    }

    /// <summary>ZstdSharp level 1 over the same hetero bytes.</summary>
    /// <returns>The compressed length.</returns>
    [Benchmark]
    public int Sharp_L1_Hetero64k()
    {
        return _sharpL1.Wrap(_hetero64k).Length;
    }

    /// <summary>ZArchiveSharp level 6 over 64 KiB of text.</summary>
    /// <returns>The compressed frame.</returns>
    [Benchmark]
    public byte[] Zar_L6_Text64k()
    {
        return _zarL6.CompressBlock(_text64k);
    }

    /// <summary>ZstdSharp level 6 over the same text bytes.</summary>
    /// <returns>The compressed length.</returns>
    [Benchmark]
    public int Sharp_L6_Text64k()
    {
        return _sharpL6.Wrap(_text64k).Length;
    }

    /// <summary>ZArchiveSharp decodes its own level-6 hetero frame.</summary>
    /// <returns>The decompressed bytes.</returns>
    [Benchmark]
    public byte[] Zar_Decode_Hetero64k()
    {
        return ZstdCompressor.DecompressFrame(_zarL6Hetero, maxSize: 65536);
    }

    /// <summary>ZstdSharp decodes the identical ZArchiveSharp-produced bytes.</summary>
    /// <returns>The decompressed length.</returns>
    [Benchmark]
    public int Sharp_Decode_Hetero64k()
    {
        return _sharpDecompressor.Unwrap(_zarL6Hetero, 65536).Length;
    }
}
