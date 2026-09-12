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

    [Fact]
    public void Pack_ParallelFactory_WorkersAreNeverEnteredConcurrently()
    {
        var root = NewTempDir("parshare");
        var zar = Path.Combine(root, "out.zar");
        var probe = new WorkerSharingProbe();

        using (var output = File.Create(zar))
        using (var writer = new ZArchiveWriter(
            output,
            compressorFactory: probe.CreateWorker,
            maxDegreeOfParallelism: 4))
        {
            var block = new byte[ZArchiveCommon.CompressedBlockSize];
            new Random(424242).NextBytes(block);
            // 32 blocks = 2 MiB: four full waves at the 8-block flush
            // threshold, so the old i % workers mapping has every chance
            // to run iterations i and i + workers concurrently.
            for (var f = 0; f < 8; f++)
            {
                Assert.True(writer.StartNewFile($"f{f}.bin"));
                for (var b = 0; b < 4; b++)
                {
                    writer.AppendData(block);
                }
            }

            writer.Finalize();
        }

        Assert.Equal(0, probe.ConcurrentEntries);
        var outDir = Path.Combine(root, "out");
        ZarPipeline.Extract(zar, outDir, new ZarPipelineOptions { MaxDegreeOfParallelism = 1 });
        Assert.True(File.Exists(Path.Combine(outDir, "f0.bin")));
    }

    private sealed class WorkerSharingProbe
    {
        private int _concurrentEntries;

        public int ConcurrentEntries => Volatile.Read(ref _concurrentEntries);

        public IZarBlockCompressor CreateWorker()
        {
            return new Worker(this);
        }

        private sealed class Worker : IZarBlockCompressor
        {
            private readonly WorkerSharingProbe _owner;
            private int _inUse;

            public Worker(WorkerSharingProbe owner)
            {
                _owner = owner;
            }

            public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
            {
                if (Interlocked.Increment(ref _inUse) != 1)
                {
                    Interlocked.Increment(ref _owner._concurrentEntries);
                }

                // Hold the worker briefly so overlap actually manifests.
                Thread.Sleep(20);
                Interlocked.Decrement(ref _inUse);
                return -1; // raw storage: valid archive, no codec dependency.
            }
        }
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

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(22)]
    public void Pack_ParallelIsByteIdentical_UncoveredLevels(int level)
    {
        var root = NewTempDir($"parlevel_{level}");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);

        var seqZar = Path.Combine(root, "seq.zar");
        var parZar = Path.Combine(root, "par.zar");
        ZarPipeline.Pack(src, seqZar, new ZarPipelineOptions
            { Level = level, MaxDegreeOfParallelism = 1 });
        ZarPipeline.Pack(src, parZar, new ZarPipelineOptions
            { Level = level, MaxDegreeOfParallelism = 8 });

        Assert.True(File.ReadAllBytes(seqZar).SequenceEqual(File.ReadAllBytes(parZar)),
            $"level {level}: parallel pack bytes differ.");
    }

    [Fact]
    public void Pack_MultiWave_ParallelIsByteIdentical()
    {
        // >2 MiB forces a second parallel wave (waveSize 32 blocks at DOP 8)
        // exercising the block+=wave / skip=0 continuation.
        var root = NewTempDir("parmultiwave");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var rng = new Random(9871);
        var big = new byte[(2 * 1024 * 1024) + (512 * 1024) + 13];
        rng.NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "wave.bin"), big);
        File.WriteAllText(Path.Combine(src, "note.txt"), "wave boundary\n");

        var seqZar = Path.Combine(root, "seq.zar");
        var parZar = Path.Combine(root, "par.zar");
        ZarPipeline.Pack(src, seqZar, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 1 });
        ZarPipeline.Pack(src, parZar, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 8 });

        Assert.True(File.ReadAllBytes(seqZar).SequenceEqual(File.ReadAllBytes(parZar)),
            "Multi-wave parallel pack bytes differ.");

        var outDir = Path.Combine(root, "out");
        ZarPipeline.Extract(parZar, outDir, new ZarPipelineOptions { MaxDegreeOfParallelism = 8 });
        AssertTreesEqual(src, outDir);
    }

    [Fact]
    public void Pack_ParallelTwice_IsDeterministic()
    {
        // Pool reuse must not leak garbage into bytes: two parallel packs
        // in the same process must be identical (and match sequential).
        var root = NewTempDir("pardeterminism");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);

        var first = Path.Combine(root, "first.zar");
        var second = Path.Combine(root, "second.zar");
        var seq = Path.Combine(root, "seq.zar");
        ZarPipeline.Pack(src, first, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 8 });
        ZarPipeline.Pack(src, second, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 8 });
        ZarPipeline.Pack(src, seq, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 1 });

        Assert.True(File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second)),
            "Two parallel packs of the same tree differ (pool reuse leak).");
        Assert.True(File.ReadAllBytes(seq).SequenceEqual(File.ReadAllBytes(first)),
            "Parallel pack differs from sequential.");
    }

    [Fact]
    public void Extract_ParallelCorruptTail_ThrowsInvalidOperationNotAggregate()
    {
        // Second-wave corruption: open still succeeds (footer intact) so the
        // failure must surface from the parallel decode path with the
        // sequential-path contract, never a raw AggregateException.
        var root = NewTempDir("partail");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var rng = new Random(4242);
        var big = new byte[(2 * 1024 * 1024) + (512 * 1024) + 13];
        rng.NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "wave.bin"), big);

        var zar = Path.Combine(root, "good.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions { Level = 6, MaxDegreeOfParallelism = 1 });

        var bytes = File.ReadAllBytes(zar);
        var broken = 0;
        var halfway = bytes.Length / 2;
        for (var i = halfway; i + 4 <= bytes.Length; i++)
        {
            if (bytes[i] == 0x28 && bytes[i + 1] == 0xB5 && bytes[i + 2] == 0x2F && bytes[i + 3] == 0xFD)
            {
                bytes[i] ^= 0xFF;
                broken++;
                break;
            }
        }

        Assert.True(broken > 0, "Fixture produced no second-half compressed frame to break.");
        var badZar = Path.Combine(root, "bad.zar");
        File.WriteAllBytes(badZar, bytes);

        var ex = Assert.ThrowsAny<Exception>(() =>
            ZarPipeline.Extract(badZar, Path.Combine(root, "out"),
                new ZarPipelineOptions { MaxDegreeOfParallelism = 8 }));
        Assert.IsNotType<AggregateException>(ex);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("Extraction failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_FaultMidEmit_PoolStaysUsable()
    {
        // A fault during the ordered emit must not double-return staged
        // buffers: a later pack in the same process must still round-trip.
        var root = NewTempDir("parfault");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateSpanning(src);

        // Direct fault injection through a throwing output: the emit path
        // must not corrupt the shared pools for later packs.
        var failStream = new FailingWriteStream(failAfterBytes: 200_000);
        try
        {
            using var failWriter = new ZArchiveWriter(
                failStream,
                compressor: null,
                maxDegreeOfParallelism: 4,
                compressorFactory: () => new Zstd.ZstdCompressor());
            foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(src, file).Replace('\\', '/');
                var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/');
                if (!string.IsNullOrEmpty(dir))
                {
                    failWriter.MakeDir(dir, recursive: true);
                }

                if (!failWriter.StartNewFile(rel))
                {
                    throw new InvalidOperationException($"Cannot create {rel}.");
                }

                failWriter.AppendData(File.ReadAllBytes(file));
            }

            Assert.Throws<IOException>(() => failWriter.Finalize());
        }
        catch (IOException)
        {
            // Expected: the failing sink threw mid-emit.
        }

        var zar = Path.Combine(root, "good.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions
            { Level = 6, MaxDegreeOfParallelism = 4 });
        var outDir = Path.Combine(root, "out");
        ZarPipeline.Extract(zar, outDir, new ZarPipelineOptions { MaxDegreeOfParallelism = 4 });
        AssertTreesEqual(src, outDir);
    }

    private sealed class FailingWriteStream : MemoryStream
    {
        private long _written;
        private readonly long _failAfterBytes;

        public FailingWriteStream(long failAfterBytes)
        {
            _failAfterBytes = failAfterBytes;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _written += count;
            if (_written > _failAfterBytes)
            {
                throw new IOException("Simulated output fault.");
            }

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _written += buffer.Length;
            if (_written > _failAfterBytes)
            {
                throw new IOException("Simulated output fault.");
            }

            base.Write(buffer);
        }
    }
}