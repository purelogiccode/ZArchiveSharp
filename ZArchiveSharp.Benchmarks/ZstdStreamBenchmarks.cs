using BenchmarkDotNet.Attributes;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Benchmarks;

[MemoryDiagnoser]
[MinIterationCount(5)]
[MaxIterationCount(20)]
public class ZstdStreamBenchmarks
{
    private byte[] _text1M = null!;
    private byte[] _hetero1M = null!;
    private byte[] _text10M = null!;
    private byte[] _sink = null!;
    private readonly Dictionary<(int Payload, int Level), byte[]> _frames = new();
    private readonly Dictionary<(int Payload, int Level), int> _frameSizes = new();

    /// <summary>Compression level under test (the ZAR hot-path levels).</summary>
    [Params(1, 6)]
    public int Level { get; set; }

    /// <summary>Pump chunk size in bytes (application read/write granularity).</summary>
    [Params(4096, 65536)]
    public int ChunkSize { get; set; }

    /// <summary>
    /// Builds the frozen payloads, pre-compresses every decode fixture and
    /// records deterministic frame sizes once per benchmark process.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _text1M = BenchmarkCorpus.CycleText(1024 * 1024);
        _hetero1M = Tile(BenchmarkCorpus.Hetero64(), 1024 * 1024);
        _text10M = BenchmarkCorpus.CycleText(10 * 1024 * 1024);
        _sink = new byte[65536];
        byte[][] payloads = [_text1M, _hetero1M, _text10M];
        foreach (var level in new[] { 1, 6 })
        {
            var compressor = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
            for (var i = 0; i < payloads.Length; i++)
            {
                var frame = compressor.CompressBlock(payloads[i]);
                _frames[(i, level)] = frame;
                _frameSizes[(i, level)] = frame.Length;
            }
        }

        Console.WriteLine(
            $"[fixtures] text1m L1={_frameSizes[(0, 1)]} L6={_frameSizes[(0, 6)]} hetero1m L1={_frameSizes[(1, 1)]} L6={_frameSizes[(1, 6)]} text10m L1={_frameSizes[(2, 1)]} L6={_frameSizes[(2, 6)]}");
    }

    private static byte[] Tile(byte[] pattern, int n)
    {
        var buf = new byte[n];
        for (var i = 0; i < n; i += pattern.Length)
        {
            pattern.CopyTo(buf, i);
        }

        return buf;
    }

    private long PumpCompress(byte[] payload, int payloadIndex)
    {
        using var ms = new MemoryStream(_frameSizes[(payloadIndex, Level)] + 64);
        using (var enc = new ZstdCompressionStream(ms, Level, leaveOpen: true))
        {
            for (var off = 0; off < payload.Length; off += ChunkSize)
            {
                enc.Write(payload, off, Math.Min(ChunkSize, payload.Length - off));
            }
        }

        return ms.Length;
    }

    private long PumpDecompress(byte[] frame)
    {
        using var ms = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(ms);
        long total = 0;
        int read;
        while ((read = dec.Read(_sink, 0, Math.Min(ChunkSize, _sink.Length))) > 0)
        {
            total += read;
        }

        return total;
    }

    /// <summary>Streams 1 MiB of text through the compression stream.</summary>
    /// <returns>The compressed frame size.</returns>
    [Benchmark]
    public long Compress_Text1M()
    {
        return PumpCompress(_text1M, 0);
    }

    /// <summary>Streams 1 MiB of tiled-hetero data through the compression stream.</summary>
    /// <returns>The compressed frame size.</returns>
    [Benchmark]
    public long Compress_Hetero1M()
    {
        return PumpCompress(_hetero1M, 1);
    }

    /// <summary>Streams 10 MiB of text through the compression stream (control).</summary>
    /// <returns>The compressed frame size.</returns>
    [Benchmark]
    public long Compress_Text10M()
    {
        return PumpCompress(_text10M, 2);
    }

    /// <summary>Reads the 1 MiB text frame back through the decompression stream.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_Text1M()
    {
        return PumpDecompress(_frames[(0, Level)]);
    }

    /// <summary>Reads the 1 MiB hetero frame back through the decompression stream.</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_Hetero1M()
    {
        return PumpDecompress(_frames[(1, Level)]);
    }

    /// <summary>Reads the 10 MiB text frame back through the decompression stream (control).</summary>
    /// <returns>The total decompressed bytes.</returns>
    [Benchmark]
    public long Decompress_Text10M()
    {
        return PumpDecompress(_frames[(2, Level)]);
    }
}