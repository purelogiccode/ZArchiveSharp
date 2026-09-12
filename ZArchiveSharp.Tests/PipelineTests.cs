using System.Diagnostics;
using ZArchiveSharp.Pipeline;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for the Step 5 archive pipeline (<see cref="ZarPipeline"/> and
/// friends): round-trips, byte-identity with <see cref="ZArchiveTool"/>,
/// progress totals, cancellation, collision policies, batches, stage
/// weights, config, file listing and pause.
/// </summary>
public sealed class PipelineTests : IDisposable
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

    private static byte[] PatternBytes(int length, int seed)
    {
        var data = new byte[length];
        var state = (uint)((seed * 2654435761u) + 1);
        for (var i = 0; i < length; i++)
        {
            state = (state * 1664525) + 1013904223;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    private static void PopulateRich(string dir)
    {
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(dir, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(dir, "big.bin"), PatternBytes(200000, 7));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "sub", "deep.txt"), new string('z', 70000));
        Directory.CreateDirectory(Path.Combine(dir, "sub", "emptydir"));
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string dir)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            map[Path.GetRelativePath(dir, file).Replace('\\', '/')] = File.ReadAllBytes(file);
        }

        return map;
    }

    private sealed class Collector : IProgress<ZarProgress>
    {
        private readonly Lock _gate = new();
        public readonly List<ZarProgress> Events = [];

        public void Report(ZarProgress value)
        {
            lock (_gate)
            {
                Events.Add(value);
            }
        }
    }

    private sealed class CancelOnFirstFile(CancellationTokenSource cts) : IProgress<ZarProgress>
    {
        private readonly CancellationTokenSource _cts = cts;

        public void Report(ZarProgress value)
        {
            if (value.FilesCompleted >= 1)
            {
                _cts.Cancel();
            }
        }
    }

    // ------------------------------------------------------------------
    // Pack / extract
    // ------------------------------------------------------------------

    [Fact]
    public void PackExtract_RoundTrip_PreservesTree()
    {
        var root = NewTempDir("pipe_rt");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");
        var dest = Path.Combine(root, "dest");

        var written = ZarPipeline.Pack(src, zar);
        Assert.Equal(zar, written);
        var files = ZarPipeline.Extract(zar, dest);

        var expected = SnapshotFiles(src);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal).ToArray(),
            files.Order(StringComparer.Ordinal).ToArray());
        var actual = SnapshotFiles(dest);
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (rel, data) in expected)
        {
            Assert.True(actual.TryGetValue(rel, out var got), $"missing {rel}");
            Assert.Equal(data, got);
        }
    }

    [Fact]
    public void DirectoryPackSource_DoesNotFollowDirectoryLinks()
    {
        var root = NewTempDir("pipe_link");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        File.WriteAllText(Path.Combine(src, "inside.txt"), "in");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "out");

        var link = Path.Combine(src, "junction");
        if (!TryCreateDirectoryLink(link, outside))
        {
            return; // Host cannot create links: nothing to assert here.
        }

        var entries = new DirectoryPackSource(src).Collect();
        Assert.Contains(entries, e => string.Equals(e.RelativePath, "inside.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RelativePath.StartsWith("junction/", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.RelativePath.Contains("secret", StringComparison.Ordinal));
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                proc!.WaitForExit(10000);
                return proc.ExitCode == 0;
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Fact]
    public void PackBatch_Progress_RebasesBytesIntoItemShare()
    {
        var root = NewTempDir("pipe_batchprog");
        var a = Directory.CreateDirectory(Path.Combine(root, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "b")).FullName;
        File.WriteAllBytes(Path.Combine(a, "big.bin"), new byte[900_000]);
        File.WriteAllBytes(Path.Combine(a, "small.bin"), new byte[100_000]);
        File.WriteAllBytes(Path.Combine(b, "only.bin"), new byte[200_000]);

        var collector = new Collector();
        ZarPipeline.PackBatch([a, b], Path.Combine(root, "dest"),
            new ZarPipelineOptions { MaxDegreeOfParallelism = 1 }, collector);

        // The first item's byte counters must be re-based into its 1/2 share:
        // item-local ratios (up to 0.9 here) would leak straight through.
        var first = collector.Events
            .Where(e => string.Equals(e.SourcePath, a, StringComparison.Ordinal) && e.BytesTotal > 0).ToList();
        Assert.NotEmpty(first);
        Assert.Contains(first, e => e.BytesCompleted > 0);
        Assert.All(first, e => Assert.True(e.Ratio <= 0.5 + 1e-9, $"item ratio leaked: {e.Ratio}"));
        Assert.Equal(1.0, collector.Events[^1].Ratio, 9);
    }

    [Fact]
    public void Pack_Overwrite_MissingSource_KeepsExistingOutput()
    {
        var root = NewTempDir("pipe_overwrite");
        var existing = Path.Combine(root, "out.zar");
        File.WriteAllBytes(existing, "keep-me"u8.ToArray());

        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Overwrite };
        Assert.Throws<DirectoryNotFoundException>(() =>
            ZarPipeline.Pack(Path.Combine(root, "missing"), existing, options));

        // A source that cannot even be collected must not destroy the
        // previous archive on the overwrite path.
        Assert.Equal("keep-me"u8.ToArray(), File.ReadAllBytes(existing));
    }

    [Fact]
    public void PackBatch_AutoRename_SameStemItems_AllSucceed()
    {
        var root = NewTempDir("pipe_autorename");
        var dest = Path.Combine(root, "dest");
        var sources = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, $"s{i}", "x")).FullName;
            File.WriteAllBytes(Path.Combine(dir, "f.bin"), new byte[100_000 + i]);
            sources.Add(dir);
        }

        var results = ZarPipeline.PackBatch(sources, dest, new ZarPipelineOptions
        {
            CollisionPolicy = ZarCollisionPolicy.AutoRename,
            MaxDegreeOfParallelism = 8,
        });

        Assert.All(results, r => Assert.Equal(ZarItemStatus.Completed, r.Status));
        Assert.Equal(8, Directory.GetFiles(dest, "x*.zar").Length);
    }

    [Fact]
    public void Pack_MatchesTool_ByteIdentical()
    {
        var root = NewTempDir("pipe_tool");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);

        var viaTool = Path.Combine(root, "tool.zar");
        var viaPipeline = Path.Combine(root, "pipe.zar");
        ZArchiveTool.Pack(src, viaTool);
        ZarPipeline.Pack(src, viaPipeline);

        Assert.Equal(File.ReadAllBytes(viaTool), File.ReadAllBytes(viaPipeline));
    }

    [Fact]
    public void Pack_ProgressTotals_AgreeWithSource()
    {
        var root = NewTempDir("pipe_prog");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var expected = SnapshotFiles(src);

        var collector = new Collector();
        ZarPipeline.Pack(src, Path.Combine(root, "out.zar"), null, collector);

        Assert.NotEmpty(collector.Events);
        var last = collector.Events[^1];
        Assert.Equal(expected.Count, last.FilesTotal);
        Assert.Equal(expected.Count, last.FilesCompleted);
        Assert.Equal(expected.Values.Sum(b => (long)b.Length), last.BytesTotal);
        Assert.Equal(last.BytesTotal, last.BytesCompleted);
        Assert.Equal(1.0, last.Ratio);
        foreach (var e in collector.Events)
        {
            Assert.InRange(e.Ratio, 0.0, 1.0);
        }

        var seen = collector.Events.Select(e => e.CurrentFile)
            .Where(s => s.Length != 0).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal).ToArray(),
            seen.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Pack_PreCancelled_ThrowsWithoutOutput()
    {
        var root = NewTempDir("pipe_cancel0");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ZarPipeline.Pack(src, zar, null, null, cts.Token));
        Assert.False(File.Exists(zar));
    }

    [Fact]
    public void Pack_MidPackCancel_DeletesIncompleteOutput()
    {
        var root = NewTempDir("pipe_cancel1");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(src, $"f{i}.bin"), PatternBytes(1000000, 1000 + i));
        }

        var zar = Path.Combine(root, "out.zar");
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            ZarPipeline.Pack(src, zar, null, new CancelOnFirstFile(cts), cts.Token));
        Assert.False(File.Exists(zar));
    }

    [Fact]
    public void Extract_PreCancelled_ThrowsWithoutOutput()
    {
        var root = NewTempDir("pipe_cancel2");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");
        ZarPipeline.Pack(src, zar);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            ZarPipeline.Extract(zar, Path.Combine(root, "dest"), null, null, cts.Token));
        Assert.False(Directory.Exists(Path.Combine(root, "dest")));
    }

    [Fact]
    public void Extract_Missing_ThrowsFileNotFound()
    {
        var root = NewTempDir("pipe_missing");
        Assert.Throws<FileNotFoundException>(() =>
            ZarPipeline.Extract(Path.Combine(root, "nope.zar"), Path.Combine(root, "dest")));
    }

    [Fact]
    public void Extract_Corrupt_ThrowsInvalidOperation()
    {
        var root = NewTempDir("pipe_corrupt");
        var zar = Path.Combine(root, "bad.zar");
        File.WriteAllBytes(zar, PatternBytes(1024, 3));
        // Garbage fails the footer-gated open (a ZarArchiveOpenException,
        // which is an InvalidOperationException for the -12 mapping).
        Assert.Throws<ZarArchiveOpenException>(() => ZarPipeline.Extract(zar, Path.Combine(root, "dest")));
    }

    [Theory]
    [InlineData(1)] // sequential writer loop
    [InlineData(4)] // parallel block-wave loop
    public void Extract_CorruptLaterBlock_LeavesNoPartialFile(int workers)
    {
        var root = NewTempDir($"pipe_partial{workers}");
        var blockSize = ZArchiveCommon.CompressedBlockSize;
        var first = new byte[blockSize];
        new Random(20260913).NextBytes(first);
        var data = new byte[blockSize * 2];
        first.CopyTo(data, 0);
        Array.Fill(data, (byte)'A', blockSize, blockSize);

        var zar = Path.Combine(root, "big.zar");
        using (var output = File.Create(zar))
        using (var writer = new ZArchiveWriter(output))
        {
            Assert.True(writer.StartNewFile("big.bin"));
            writer.AppendData(data);
            writer.Finalize();
        }

        // Block 0 is stored raw (locatable); corrupt block 1's frame magic so
        // the second read fails after the first block was already written.
        var bytes = File.ReadAllBytes(zar);
        var block0 = bytes.AsSpan().IndexOf(first.AsSpan(0, 32));
        Assert.True(block0 >= 0, "Could not locate the raw first block in the archive.");
        bytes[block0 + blockSize] ^= 0xFF;
        File.WriteAllBytes(zar, bytes);

        var dest = Path.Combine(root, "out");
        var options = new ZarPipelineOptions { MaxDegreeOfParallelism = workers };
        Assert.Throws<InvalidOperationException>(() => ZarPipeline.Extract(zar, dest, options));

        // The scratch file (and any partial output) must be gone.
        Assert.False(File.Exists(Path.Combine(dest, "big.bin")));
        Assert.Empty(Directory.GetFiles(dest, "big.bin", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(dest, "*.part", SearchOption.AllDirectories));
    }

    [Fact]
    public void Extract_DeeplyNestedArchive_FailsCatchably()
    {
        var root = NewTempDir("pipe_deep");
        var depth = ZarPackEngine.MaxExtractDepth + 10;
        var nested = string.Join('/', Enumerable.Repeat("d", depth));

        var zar = Path.Combine(root, "deep.zar");
        using (var output = File.Create(zar))
        using (var writer = new ZArchiveWriter(output))
        {
            Assert.True(writer.MakeDir(nested, recursive: true));
            Assert.True(writer.StartNewFile(nested + "/f.txt"));
            writer.AppendData([1]);
            writer.Finalize();
        }

        // Bounded recursion: a crafted deep tree must throw catchably instead
        // of overflowing the stack.
        var ex = Assert.Throws<InvalidOperationException>(() => ZarPipeline.Extract(zar, Path.Combine(root, "out")));
        Assert.Contains("depth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Collision policies
    // ------------------------------------------------------------------

    [Fact]
    public void Collision_Fail_ThrowsLikeZarchive()
    {
        var root = NewTempDir("pipe_fail");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");
        ZarPipeline.Pack(src, zar);

        var ex = Assert.Throws<IOException>(() => ZarPipeline.Pack(src, zar));
        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Collision_Skip_ReturnsNullAndKeepsOriginal()
    {
        var root = NewTempDir("pipe_skip");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");
        ZarPipeline.Pack(src, zar);
        var before = File.ReadAllBytes(zar);

        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Skip };
        Assert.Null(ZarPipeline.Pack(src, zar, options));
        Assert.Equal(before, File.ReadAllBytes(zar));
    }

    [Fact]
    public void Collision_Overwrite_Replaces()
    {
        var root = NewTempDir("pipe_over");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");
        ZarPipeline.Pack(src, zar);

        File.WriteAllText(Path.Combine(src, "added.txt"), new string('q', 50000));
        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Overwrite };
        Assert.Equal(zar, ZarPipeline.Pack(src, zar, options));

        var dest = Path.Combine(root, "dest");
        var files = ZarPipeline.Extract(zar, dest);
        Assert.Contains("added.txt", files, StringComparer.Ordinal);
    }

    [Fact]
    public void Collision_AutoRename_NumbersSiblings()
    {
        var root = NewTempDir("pipe_rename");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.AutoRename };

        var first = ZarPipeline.Pack(src, Path.Combine(root, "out.zar"), options);
        var second = ZarPipeline.Pack(src, Path.Combine(root, "out.zar"), options);
        var third = ZarPipeline.Pack(src, Path.Combine(root, "out.zar"), options);

        Assert.Equal(Path.Combine(root, "out.zar"), first);
        Assert.Equal(Path.Combine(root, "out_1.zar"), second);
        Assert.Equal(Path.Combine(root, "out_2.zar"), third);
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public void PackSource_HonorsCollisionPolicyAndCreatesOutputDirectory()
    {
        var root = NewTempDir("pipe_src");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var source = new DirectoryPackSource(src);
        var zar = Path.Combine(root, "newdir", "out.zar");

        // The output directory is created instead of failing CreateNew.
        ZarPipeline.PackSource(source, zar);
        Assert.True(File.Exists(zar));
        var first = File.ReadAllBytes(zar);

        // Fail (default) still refuses.
        Assert.Throws<IOException>(() => ZarPipeline.PackSource(source, zar));

        // Skip writes nothing and keeps the original.
        ZarPipeline.PackSource(source, zar, new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Skip });
        Assert.Equal(first, File.ReadAllBytes(zar));

        // AutoRename numbers a sibling.
        ZarPipeline.PackSource(source, zar,
            new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.AutoRename });
        Assert.True(File.Exists(Path.Combine(root, "newdir", "out_1.zar")));

        // Overwrite replaces.
        ZarPipeline.PackSource(source, zar,
            new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Overwrite });
        Assert.True(File.Exists(zar));
    }

    // ------------------------------------------------------------------
    // Batches
    // ------------------------------------------------------------------

    [Fact]
    public void PackBatch_Parallel_CompletesAll()
    {
        var root = NewTempDir("pipe_batch");
        var sources = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var src = Directory.CreateDirectory(Path.Combine(root, $"src{i}")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), $"hello {i} " + new string('x', 20000));
            sources.Add(src);
        }

        var dest = Path.Combine(root, "zars");
        var collector = new Collector();
        var options = new ZarPipelineOptions { MaxDegreeOfParallelism = 2 };
        var results = ZarPipeline.PackBatch(sources, dest, options, collector);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(ZarItemStatus.Completed, r.Status));
        Assert.Equal(ZarProcessState.Completed, ZarPipeline.RollUp(results));
        foreach (var r in results)
        {
            Assert.NotNull(r.DestinationPath);
            Assert.True(File.Exists(r.DestinationPath));
        }

        Assert.NotEmpty(collector.Events);
    }

    [Fact]
    public void PackBatch_Isolation_FailedItemDoesNotStopOthers()
    {
        var root = NewTempDir("pipe_isol");
        var good = Directory.CreateDirectory(Path.Combine(root, "good")).FullName;
        PopulateRich(good);
        var missing = Path.Combine(root, "missing");

        var results = ZarPipeline.PackBatch([good, missing], Path.Combine(root, "zars"));

        Assert.Equal(2, results.Count);
        Assert.Equal(ZarItemStatus.Completed, results[0].Status);
        Assert.Equal(ZarItemStatus.Failed, results[1].Status);
        Assert.NotNull(results[1].ErrorMessage);
        Assert.Equal(ZarProcessState.Failed, ZarPipeline.RollUp(results));
    }

    [Fact]
    public void PackBatch_UnexpectedFault_IsIsolatedPerItem()
    {
        var root = NewTempDir("pipe_fault");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var options = new ZarPipelineOptions
        {
            Compressor = new ThrowingCompressor(),
            MaxDegreeOfParallelism = 2,
        };

        // A compressor fault is not one of the I/O fault types: it must still
        // come back as this item's ZarItemResult, not escape as
        // AggregateException from Parallel.For.
        var results = ZarPipeline.PackBatch([src, src], Path.Combine(root, "zars"), options);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ZarItemStatus.Failed, r.Status));
        Assert.All(results, r => Assert.NotNull(r.ErrorMessage));
    }

    private sealed class ThrowingCompressor : IZarBlockCompressor
    {
        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            throw new ZstdException("compressor exploded");
        }
    }

    [Fact]
    public void PackBatch_SkipPolicy_RollsUpPartial()
    {
        var root = NewTempDir("pipe_pskip");
        var dest = Directory.CreateDirectory(Path.Combine(root, "zars")).FullName;
        var sources = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var src = Directory.CreateDirectory(Path.Combine(root, $"src{i}")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), "data " + new string('y', 10000));
            sources.Add(src);
        }

        // Pre-create the first output so it skips.
        ZarPipeline.Pack(sources[0], Path.Combine(dest, "src0.zar"));

        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Skip };
        var results = ZarPipeline.PackBatch(sources, dest, options);

        Assert.Equal(ZarItemStatus.Skipped, results[0].Status);
        Assert.Equal(ZarItemStatus.Completed, results[1].Status);
        Assert.Equal(ZarProcessState.Partial, ZarPipeline.RollUp(results));
    }

    [Fact]
    public void PackBatch_PreCancelled_AllCancelled()
    {
        var root = NewTempDir("pipe_pcancel");
        var sources = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var src = Directory.CreateDirectory(Path.Combine(root, $"src{i}")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), "data");
            sources.Add(src);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var results = ZarPipeline.PackBatch(sources, Path.Combine(root, "zars"), null, null, cts.Token);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(ZarItemStatus.Cancelled, r.Status));
        Assert.Equal(ZarProcessState.Cancelled, ZarPipeline.RollUp(results));
    }

    [Fact]
    public void PackBatch_DeleteSourceOnSuccess_RemovesPackedDirs()
    {
        var root = NewTempDir("pipe_del");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);

        var options = new ZarPipelineOptions { DeleteSourceOnSuccess = true };
        var results = ZarPipeline.PackBatch([src], Path.Combine(root, "zars"), options);

        Assert.Equal(ZarItemStatus.Completed, results[0].Status);
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void Pack_DeleteSourceOnSuccess_RemovesSource()
    {
        var root = NewTempDir("pipe_del1");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);

        var options = new ZarPipelineOptions { DeleteSourceOnSuccess = true };
        var written = ZarPipeline.Pack(src, Path.Combine(root, "out.zar"), options);

        Assert.NotNull(written);
        Assert.True(File.Exists(written));
        Assert.False(Directory.Exists(src));
    }

    [Fact]
    public void PackBatch_Empty_ReturnsCompletedRollUp()
    {
        var results = ZarPipeline.PackBatch([], NewTempDir("pipe_empty"));
        Assert.Empty(results);
        Assert.Equal(ZarProcessState.Completed, ZarPipeline.RollUp(results));
    }

    [Fact]
    public void ExtractBatch_RoundTrips()
    {
        var root = NewTempDir("pipe_eb");
        var zars = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var src = Directory.CreateDirectory(Path.Combine(root, $"src{i}")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), $"payload {i}");
            zars.Add(ZarPipeline.Pack(src, Path.Combine(root, $"a{i}.zar"))!);
        }

        var results = ZarPipeline.ExtractBatch(zars, Path.Combine(root, "out"));
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(ZarItemStatus.Completed, r.Status));
        Assert.Equal("payload 0", File.ReadAllText(Path.Combine(root, "out", "a0_extracted", "a.txt")));
        Assert.Equal("payload 1", File.ReadAllText(Path.Combine(root, "out", "a1_extracted", "a.txt")));
    }

    [Fact]
    public void ExtractBatch_SameStemArchives_UseDistinctDestinations()
    {
        var root = NewTempDir("pipe_ebsame");
        var zars = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var src = Directory.CreateDirectory(Path.Combine(root, $"src{i}")).FullName;
            File.WriteAllText(Path.Combine(src, "payload.txt"), $"payload {i}");
            var sub = Directory.CreateDirectory(Path.Combine(root, $"s{i}")).FullName;
            zars.Add(ZarPipeline.Pack(src, Path.Combine(sub, "game.zar"))!);
        }

        var results = ZarPipeline.ExtractBatch(zars, Path.Combine(root, "out"),
            new ZarPipelineOptions { MaxDegreeOfParallelism = 2 });

        Assert.All(results, r => Assert.Equal(ZarItemStatus.Completed, r.Status));
        Assert.NotEqual(results[0].DestinationPath, results[1].DestinationPath, StringComparer.Ordinal);
        Assert.Equal("payload 0", File.ReadAllText(Path.Combine(results[0].DestinationPath!, "payload.txt")));
        Assert.Equal("payload 1", File.ReadAllText(Path.Combine(results[1].DestinationPath!, "payload.txt")));
    }

    [Fact]
    public void ExtractBatch_SingleItem_UsesDestDirectly()
    {
        var root = NewTempDir("pipe_eb1");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "solo");
        var zar = ZarPipeline.Pack(src, Path.Combine(root, "a.zar"))!;

        var dest = Path.Combine(root, "out");
        var results = ZarPipeline.ExtractBatch([zar], dest);
        Assert.Equal(ZarItemStatus.Completed, results[0].Status);
        Assert.Equal("solo", File.ReadAllText(Path.Combine(dest, "a.txt")));
    }

    // ------------------------------------------------------------------
    // Weights / config / file listing / pause
    // ------------------------------------------------------------------

    [Fact]
    public void StageWeights_MatchCorePy()
    {
        var root = NewTempDir("pipe_w");
        var zip = Path.Combine(root, "a.zip");
        File.WriteAllBytes(zip, [1, 2, 3]);
        var iso = Path.Combine(root, "b.iso");
        File.WriteAllBytes(iso, [1, 2, 3]);
        var dir = Directory.CreateDirectory(Path.Combine(root, "d")).FullName;

        var archive = ZarStageWeights.ForFile(zip, ZarProcessMode.Auto);
        Assert.Equal(
            [("7z", 0.0, 0.33), ("xiso", 0.33, 0.33), ("zar", 0.66, 0.34)],
            archive.Select(s => (s.Stage, s.Base, s.Length)));

        var image = ZarStageWeights.ForFile(iso, ZarProcessMode.Auto);
        Assert.Equal(
            [("xiso", 0.0, 0.5), ("zar", 0.5, 0.5)],
            image.Select(s => (s.Stage, s.Base, s.Length)));

        var folder = ZarStageWeights.ForFile(dir, ZarProcessMode.Auto);
        var single = Assert.Single(folder);
        Assert.Equal(("zar", 0.0, 1.0), (single.Stage, single.Base, single.Length));

        var flat = ZarStageWeights.ForFile(zip, ZarProcessMode.Compress);
        var only = Assert.Single(flat);
        Assert.Equal(("all", 0.0, 1.0), (only.Stage, only.Base, only.Length));

        Assert.Equal(0.33 + (0.33 * 0.5), ZarStageWeights.Rebase(archive, "xiso", 0.5), 9);
        Assert.Equal(0.25, ZarStageWeights.Rebase(archive, "unknown", 0.25), 9);
    }

    [Fact]
    public void Config_SaveLoad_RoundTrip()
    {
        var root = NewTempDir("pipe_cfg");
        var config = new ZarSettings
        {
            SourceDir = @"C:\in",
            TargetDir = @"C:\out",
            Workers = 8,
            Language = "en",
            CollisionPolicy = ZarCollisionPolicy.AutoRename,
            Mode = ZarProcessMode.Compress,
        };
        config.Save(root);

        var loaded = ZarSettings.Load(root);
        Assert.Equal(config, loaded);

        var options = loaded.ToPipelineOptions();
        Assert.Equal(8, options.MaxDegreeOfParallelism);
        Assert.Equal(ZarCollisionPolicy.AutoRename, options.CollisionPolicy);
    }

    [Fact]
    public void Config_LoadMissing_ReturnsDefaults()
    {
        var loaded = ZarSettings.Load(NewTempDir("pipe_cfgmiss"));
        Assert.Equal(new ZarSettings(), loaded);
        Assert.Equal(4, loaded.Workers);
        Assert.Equal("pt-br", loaded.Language);
    }

    [Fact]
    public void ProcessableFiles_FiltersByMode()
    {
        var root = NewTempDir("pipe_files");
        foreach (var name in new[] { "a.zip", "b.rar", "c.7z", "d.tar", "e.gz", "f.iso", "g.txt" })
        {
            File.WriteAllText(Path.Combine(root, name), "x");
        }

        Directory.CreateDirectory(Path.Combine(root, "sub"));

        var auto = ProcessableFiles.Find(root, ZarProcessMode.Auto);
        Assert.Equal(
            (string[])["a.zip", "b.rar", "c.7z", "d.tar", "e.gz", "f.iso", "sub"],
            auto.Select(p => Path.GetFileName(p)).ToArray());

        var archives = ProcessableFiles.Find(root, ZarProcessMode.ExtractArchive);
        Assert.Equal((string[])["a.zip", "b.rar", "c.7z", "d.tar", "e.gz"],
            archives.Select(p => Path.GetFileName(p)).ToArray());

        var iso = ProcessableFiles.Find(root, ZarProcessMode.ExtractIso);
        Assert.Equal((string[])["f.iso"], iso.Select(p => Path.GetFileName(p)).ToArray());

        var dirs = ProcessableFiles.Find(root, ZarProcessMode.Compress);
        Assert.Equal((string[])["sub"], dirs.Select(p => Path.GetFileName(p)).ToArray());
    }

    [Theory]
    [InlineData("auto", ZarProcessMode.Auto)]
    [InlineData("AUTO", ZarProcessMode.Auto)]
    [InlineData("extract-archive", ZarProcessMode.ExtractArchive)]
    [InlineData("extract-arc", ZarProcessMode.ExtractArchive)]
    [InlineData("archive", ZarProcessMode.ExtractArchive)]
    [InlineData("ARCHIVE", ZarProcessMode.ExtractArchive)]
    [InlineData("extract-iso", ZarProcessMode.ExtractIso)]
    [InlineData("extract", ZarProcessMode.ExtractIso)]
    [InlineData("iso", ZarProcessMode.ExtractIso)]
    [InlineData("ISO", ZarProcessMode.ExtractIso)]
    [InlineData("compress", ZarProcessMode.Compress)]
    [InlineData("Compress", ZarProcessMode.Compress)]
    [InlineData(" compress ", ZarProcessMode.Compress)]
    public void ProcessModes_TryParse_Aliases(string value, ZarProcessMode expected)
    {
        // Locks the --mode flag mapping: the CLI
        // batch path filters via ProcessableFiles.Find with this result.
        Assert.True(ZarProcessModes.TryParse(value, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus")]
    [InlineData("auto,compress")]
    [InlineData("extract_archive")]
    [InlineData("extract iso")]
    public void ProcessModes_TryParse_Rejects(string? value)
    {
        Assert.False(ZarProcessModes.TryParse(value, out _));
    }

    [Fact]
    public void BatchRequest_KeepOriginals_MapsToDeleteSource()
    {
        // The --keep-originals (default) / --delete-source pair is last-wins
        // in the CLI and lands here: keeping never deletes, deleting does.
        var keep = new ZarBatchRequest(["a"], @"C:\out", KeepOriginals: true);
        Assert.False(keep.ToPipelineOptions().DeleteSourceOnSuccess);
        var drop = new ZarBatchRequest(["a"], @"C:\out", KeepOriginals: false);
        Assert.True(drop.ToPipelineOptions().DeleteSourceOnSuccess);
    }

    [Fact]
    public void Pause_DefaultToken_NeverBlocks()
    {
        var token = new PauseToken();
        Assert.False(token.IsPaused);
        token.WaitIfPaused();
    }

    [Fact]
    public void Pause_PausedWithCancelledToken_Throws()
    {
        var source = new PauseTokenSource();
        source.Pause();
        Assert.True(source.IsPaused);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => source.Token.WaitIfPaused(cts.Token));
        source.Resume();
        Assert.False(source.IsPaused);
    }

    [Fact]
    public async Task Pack_PausedThenResumed_Completes()
    {
        var root = NewTempDir("pipe_pause");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        PopulateRich(src);
        var zar = Path.Combine(root, "out.zar");

        var gate = new PauseTokenSource();
        gate.Pause();
        var options = new ZarPipelineOptions { Pause = gate.Token };
        var task = Task.Run(() => ZarPipeline.Pack(src, zar, options));
        Assert.False(task.WaitAsync(TimeSpan.FromSeconds(5)).IsCompleted);
        gate.Resume();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(zar, result);
        Assert.True(File.Exists(zar));
    }

    [Fact]
    public void BatchRequest_ExpandsToOptions()
    {
        var request = new ZarBatchRequest(["a", "b"], @"C:\out", ZarProcessMode.Compress,
            KeepOriginals: false, Policy: ZarCollisionPolicy.Skip, MaxWorkers: 2);
        var options = request.ToPipelineOptions();
        Assert.Equal(2, options.MaxDegreeOfParallelism);
        Assert.Equal(ZarCollisionPolicy.Skip, options.CollisionPolicy);
        Assert.True(options.DeleteSourceOnSuccess);
    }
}