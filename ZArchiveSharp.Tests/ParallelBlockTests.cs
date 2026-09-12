using ZArchiveSharp.Pipeline;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for block-level parallelism (<see
/// cref="ZarPipelineOptions.MaxDegreeOfParallelism"/> fan-out inside a single
/// pack/extract): parallel output must be byte-identical to sequential,
/// foreign compressors must stay single-threaded, and the error contracts
/// (corruption, cancellation) must survive the thread hop unwrapped.
/// </summary>
public sealed class ParallelBlockTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static void PopulateSpanning(string dir)
    {
        // Boundary straddlers (unaligned skips/tails) plus multi-block files
        // so every wave/partial shape in the parallel paths is exercised.
        var rng = new Random(1234);
        WriteRandom(Path.Combine(dir, "b63k.bin"), (64 * 1024) - 1, rng);
        WriteRandom(Path.Combine(dir, "b64k.bin"), 64 * 1024, rng);
        WriteRandom(Path.Combine(dir, "b64k1.bin"), (64 * 1024) + 1, rng);
        WriteRandom(Path.Combine(dir, "big.bin"), 300000, rng);
        File.WriteAllText(Path.Combine(dir, "text.txt"),
            string.Concat(Enumerable.Repeat("the quick brown fox 0123456789\n", 4000)));
        File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        WriteRandom(Path.Combine(dir, "sub", "deep.bin"), 150000, rng);
        return;

        static void WriteRandom(string path, int length, Random rng)
        {
            var bytes = new byte[length];
            rng.NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static void AssertTreesEqual(string expected, string actual)
    {
        var expectedFiles = Directory.EnumerateFiles(expected, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expected, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var actualFiles = Directory.EnumerateFiles(actual, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actual, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedFiles, actualFiles);
        foreach (var rel in expectedFiles)
        {
            Assert.True(File.ReadAllBytes(Path.Combine(expected, rel))
                .SequenceEqual(File.ReadAllBytes(Path.Combine(actual, rel))), rel);
        }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 8)]
    [InlineData(3, 8)]
    [InlineData(6, 2)]
    [InlineData(6, 4)]
    [InlineData(6, 8)]
    [InlineData(6, 16)]
    [InlineData(19, 8)]
    public void Pack_ParallelIsByteIdenticalToSequential(int level, int workers)
    {
        var root = NewTempDir($"parpack_{level}_{workers}");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);

        var seqZar = Path.Combine(root, "seq.zar");
        var parZar = Path.Combine(root, "par.zar");
        ZarPipeline.Pack(src, seqZar, new ZarPipelineOptions
            { Level = level, MaxDegreeOfParallelism = 1 });
        ZarPipeline.Pack(src, parZar, new ZarPipelineOptions
            { Level = level, MaxDegreeOfParallelism = workers });

        Assert.True(File.ReadAllBytes(seqZar).SequenceEqual(File.ReadAllBytes(parZar)),
            $"level {level} workers {workers}: parallel pack bytes differ.");

        // Both archives extract to the source tree under either DOP.
        foreach (var dop in new[] { 1, workers })
        {
            var outSeq = Path.Combine(root, $"out_seq_{dop}");
            var outPar = Path.Combine(root, $"out_par_{dop}");
            ZarPipeline.Extract(seqZar, outSeq, new ZarPipelineOptions { MaxDegreeOfParallelism = dop });
            ZarPipeline.Extract(parZar, outPar, new ZarPipelineOptions { MaxDegreeOfParallelism = dop });
            AssertTreesEqual(src, outSeq);
            AssertTreesEqual(src, outPar);
        }
    }

    [Fact]
    public void Pack_ParallelWithChecksumAndDictionary_RoundTrips()
    {
        var root = NewTempDir("pardict");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);
        var dict = Zstd.ZstdDictionary.FromBytes(
            "the quick brown fox jumps over lazy dictionaries 0123456789"u8.ToArray());

        var seqZar = Path.Combine(root, "seq.zar");
        var parZar = Path.Combine(root, "par.zar");
        ZarPipeline.Pack(src, seqZar, new ZarPipelineOptions
            { Level = 6, Checksum = true, Dictionary = dict, MaxDegreeOfParallelism = 1 });
        ZarPipeline.Pack(src, parZar, new ZarPipelineOptions
            { Level = 6, Checksum = true, Dictionary = dict, MaxDegreeOfParallelism = 8 });

        Assert.True(File.ReadAllBytes(seqZar).SequenceEqual(File.ReadAllBytes(parZar)),
            "Parallel dict/checksum pack bytes differ.");

        var extractOptions = new ZarPipelineOptions { Dictionary = dict, MaxDegreeOfParallelism = 8 };
        var done = Path.Combine(root, "out");
        ZarPipeline.Extract(parZar, done, extractOptions);
        AssertTreesEqual(src, done);
    }

    [Fact]
    public void Pack_CustomCompressor_StaysSingleThreadedAndCorrect()
    {
        var root = NewTempDir("parcustom");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);

        var probe = new ThreadTrackingCompressor();
        var parZar = Path.Combine(root, "par.zar");
        ZarPipeline.Pack(src, parZar, new ZarPipelineOptions
            { Compressor = probe, MaxDegreeOfParallelism = 8 });

        // Foreign compressors never leave the sequential path: one thread,
        // and output identical to an explicit DOP=1 pack.
        Assert.Single(probe.ThreadIds);
        var seqZar = Path.Combine(root, "seq.zar");
        ZarPipeline.Pack(src, seqZar, new ZarPipelineOptions
            { Compressor = new ThreadTrackingCompressor(), MaxDegreeOfParallelism = 1 });
        Assert.True(File.ReadAllBytes(seqZar).SequenceEqual(File.ReadAllBytes(parZar)),
            "Custom-compressor parallel pack bytes differ.");

        var done = Path.Combine(root, "out");
        ZarPipeline.Extract(parZar, done, new ZarPipelineOptions { MaxDegreeOfParallelism = 8 });
        AssertTreesEqual(src, done);
    }

    [Fact]
    public void Extract_ParallelCorrupt_ThrowsExtractionFailed()
    {
        var root = NewTempDir("parcorrupt");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);
        var zar = Path.Combine(root, "good.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions { Level = 6, MaxDegreeOfParallelism = 1 });

        // Break every zstd frame magic in the archive: at least the real
        // text-block frames fail to decode (raw-block bytes just change
        // silently, which is legitimate). Open still succeeds (footer/TOC
        // carry no such magic), so the failure must surface from the
        // parallel decode path with the sequential-path contract.
        var bytes = File.ReadAllBytes(zar);
        var broken = 0;
        for (var i = 0; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == 0x28 && bytes[i + 1] == 0xB5 && bytes[i + 2] == 0x2F && bytes[i + 3] == 0xFD)
            {
                bytes[i] ^= 0xFF;
                broken++;
            }
        }

        Assert.True(broken > 0, "Fixture produced no compressed frames to break.");
        var badZar = Path.Combine(root, "bad.zar");
        File.WriteAllBytes(badZar, bytes);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ZarPipeline.Extract(badZar, Path.Combine(root, "out"),
                new ZarPipelineOptions { MaxDegreeOfParallelism = 8 }));
        Assert.Contains("Extraction failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parallel_PackAndExtract_HonorCancellation()
    {
        var root = NewTempDir("parcancel");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);
        var zar = Path.Combine(root, "good.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions { Level = 6, MaxDegreeOfParallelism = 1 });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // Cancellation surfaces raw (never wrapped in AggregateException).
        Assert.Throws<OperationCanceledException>(() =>
            ZarPipeline.Pack(src, Path.Combine(root, "cancelled.zar"),
                new ZarPipelineOptions { MaxDegreeOfParallelism = 8 }, cancellationToken: cts.Token));
        Assert.Throws<OperationCanceledException>(() =>
            ZarPipeline.Extract(zar, Path.Combine(root, "cancelled_out"),
                new ZarPipelineOptions { MaxDegreeOfParallelism = 8 }, cancellationToken: cts.Token));
    }

    private sealed class ThreadTrackingCompressor : IZarBlockCompressor
    {
        private readonly HashSet<int> _threads = [];
        private readonly Lock _gate = new();

        public IReadOnlySet<int> ThreadIds
        {
            get
            {
                lock (_gate)
                {
                    return _threads.ToHashSet();
                }
            }
        }

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            lock (_gate)
            {
                _threads.Add(Environment.CurrentManagedThreadId);
            }

            return -1; // store raw
        }
    }
}