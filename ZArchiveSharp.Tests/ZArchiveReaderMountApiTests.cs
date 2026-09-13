namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for the reader APIs added for mount hosts: open-failure reasons,
/// node-handle directory enumeration, canonical names, entry streams, options,
/// and the concurrent read path.
/// </summary>
public sealed class ZArchiveReaderMountApiTests
{
    private static byte[] PatternBytes(int length, int seed = 0)
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

    private static byte[] BuildArchive(Action<ZArchiveWriter> build)
    {
        using var ms = new MemoryStream();
        using (var writer = new ZArchiveWriter(ms))
        {
            build(writer);
            writer.Finalize();
        }

        return ms.ToArray();
    }

    private static string WriteTempArchive(byte[] data)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mount_" + Guid.NewGuid().ToString("N") + ".zar");
        File.WriteAllBytes(path, data);
        return path;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------
    // Open failure reasons and options
    // ------------------------------------------------------------------

    [Fact]
    public void OpenFailure_ReportsSpecificReasons()
    {
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a.txt"));
            w.AppendData([1, 2, 3]);
        });

        Assert.Null(ZArchiveReader.TryOpen("definitely-missing-xyz.zar", out var missing));
        Assert.Equal(ZArchiveOpenFailure.FileNotFound, missing);

        Assert.Null(ZArchiveReader.TryOpen(new byte[16], out var small));
        Assert.Equal(ZArchiveOpenFailure.TooSmall, small);

        Assert.Null(ZArchiveReader.TryOpen(new byte[1024], out var magic));
        Assert.Equal(ZArchiveOpenFailure.BadMagic, magic);

        // Footer layout: totalSize at footer+128, version at footer+136, magic at footer+140.
        var badVersion = (byte[])zar.Clone();
        badVersion[^8] ^= 0xFF;
        Assert.Null(ZArchiveReader.TryOpen(badVersion, out var version));
        Assert.Equal(ZArchiveOpenFailure.UnsupportedVersion, version);

        var badTotal = (byte[])zar.Clone();
        badTotal[^12] ^= 0xFF;
        Assert.Null(ZArchiveReader.TryOpen(badTotal, out var total));
        Assert.Equal(ZArchiveOpenFailure.LengthMismatch, total);

        // Name-table OffsetInfo starts at footer+32; push it past the file end.
        var badSection = (byte[])zar.Clone();
        ZArchiveCommon.WriteU64Be(
            badSection.AsSpan(badSection.Length - Footer.SizeOnDisk + 32), (ulong)badSection.Length + 4096);
        Assert.Null(ZArchiveReader.TryOpen(badSection, out var section));
        Assert.Equal(ZArchiveOpenFailure.SectionOutOfRange, section);

        // File-tree size is the second u64 of the fourth OffsetInfo (footer+56).
        var badTree = (byte[])zar.Clone();
        ZArchiveCommon.WriteU64Be(badTree.AsSpan(badTree.Length - Footer.SizeOnDisk + 56), 1UL);
        Assert.Null(ZArchiveReader.TryOpen(badTree, out var tree));
        Assert.Equal(ZArchiveOpenFailure.BadFileTree, tree);

        using (var reader = ZArchiveReader.TryOpen(zar, out var ok))
        {
            Assert.NotNull(reader);
            Assert.Equal(ZArchiveOpenFailure.None, ok);
        }

        using (var nonSeekable = new NonSeekableStream(new MemoryStream(zar, writable: false)))
        {
            Assert.Null(ZArchiveReader.TryOpen(nonSeekable, leaveOpen: true, out var invalid));
            Assert.Equal(ZArchiveOpenFailure.InvalidStream, invalid);
        }
    }

    [Fact]
    public void OpenFailure_PathWithSharedReadWrite_AllowsConcurrentOpens()
    {
        var path = WriteTempArchive(BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a.txt"));
            w.AppendData([1]);
        }));

        try
        {
            var options = new ZArchiveReaderOptions { FileShare = FileShare.ReadWrite };
            using var first = ZArchiveReader.TryOpen(path, options, out var firstFailure);
            using var second = ZArchiveReader.TryOpen(path, options, out var secondFailure);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.Equal(ZArchiveOpenFailure.None, firstFailure);
            Assert.Equal(ZArchiveOpenFailure.None, secondFailure);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Options_CacheBlockCount_MustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZArchiveReaderOptions { CacheBlockCount = 0 });
        Assert.Equal(64, ZArchiveReaderOptions.Default.CacheBlockCount);
    }

    // ------------------------------------------------------------------
    // Node handles and canonical names
    // ------------------------------------------------------------------

    [Fact]
    public void TryGetDirEntry_ReturnsChildNodeHandles()
    {
        var data = PatternBytes(70000, 3);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("sub", recursive: true));
            Assert.True(w.StartNewFile("root.txt"));
            w.AppendData("root"u8);
            Assert.True(w.StartNewFile("sub/data.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        Assert.Equal(ZArchiveReader.RootNode, reader.LookUp("/"));
        Assert.Equal(2u, reader.GetDirEntryCount(ZArchiveReader.RootNode));

        uint? subNode = null;
        for (uint i = 0; i < reader.GetDirEntryCount(ZArchiveReader.RootNode); i++)
        {
            Assert.True(reader.TryGetDirEntry(ZArchiveReader.RootNode, i, out var child, out var entry));
            Assert.Equal(reader.LookUp(entry.Name), child);
            Assert.True(reader.IsFile(child) || reader.IsDirectory(child));
            if (entry.IsDirectory)
            {
                subNode = child;
            }
        }

        Assert.NotNull(subNode);
        Assert.True(reader.TryGetDirEntry(subNode.Value, 0, out var dataNode, out var dataEntry));
        Assert.Equal("data.bin", dataEntry.Name);
        Assert.True(reader.IsFile(dataNode));

        var buffer = new byte[data.Length];
        Assert.Equal((ulong)data.Length, reader.ReadFromFile(dataNode, 0, buffer));
        Assert.Equal(data, buffer);

        Assert.False(reader.TryGetDirEntry(ZArchiveReader.RootNode, 99, out _, out _));
        Assert.False(reader.TryGetDirEntry(ZArchiveReader.InvalidNode, 0, out _, out _));
    }

    [Fact]
    public void GetDirEntryCount_ClampsCraftedCounts()
    {
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("a.txt"));
            w.AppendData([1]);
        });

        var footer = Footer.ReadFrom(zar.AsSpan(zar.Length - Footer.SizeOnDisk));
        var treeOffset = (int)footer.SectionFileTree.Offset;
        var tableLength = (int)(footer.SectionFileTree.Size / (ulong)FileDirectoryEntry.SizeOnDisk);

        // Root: NodeStartIndex beyond the table -> no children at all.
        var wrap = (byte[])zar.Clone();
        ZArchiveCommon.WriteU32Be(wrap.AsSpan(treeOffset + 4), 0xFFFFFFF0u);
        ZArchiveCommon.WriteU32Be(wrap.AsSpan(treeOffset + 8), 32u);
        using (var reader = ZArchiveReader.TryOpen(wrap)!)
        {
            Assert.Equal(0u, reader.GetDirEntryCount(ZArchiveReader.RootNode));
            Assert.False(reader.TryGetDirEntry(ZArchiveReader.RootNode, 0, out _, out _));
        }

        // Root: range starts inside the table but runs past it -> clamped to the rest.
        var partial = (byte[])zar.Clone();
        ZArchiveCommon.WriteU32Be(partial.AsSpan(treeOffset + 4), (uint)(tableLength - 1));
        ZArchiveCommon.WriteU32Be(partial.AsSpan(treeOffset + 8), 100u);
        using (var reader = ZArchiveReader.TryOpen(partial)!)
        {
            Assert.Equal(1u, reader.GetDirEntryCount(ZArchiveReader.RootNode));
            Assert.False(reader.TryGetDirEntry(ZArchiveReader.RootNode, 1, out _, out _));
        }
    }

    [Fact]
    public void TryGetNodeName_ReturnsCanonicalCasing()
    {
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("Docs", recursive: true));
            Assert.True(w.StartNewFile("Docs/ReadMe.TXT"));
            w.AppendData([1]);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        var node = reader.LookUp("docs/readme.txt");
        Assert.NotEqual(ZArchiveReader.InvalidNode, node);
        Assert.True(reader.TryGetNodeName(node, out var name));
        Assert.Equal("ReadMe.TXT", name);

        Assert.True(reader.TryGetNodeName(ZArchiveReader.RootNode, out var rootName));
        Assert.Equal(string.Empty, rootName);

        Assert.False(reader.TryGetNodeName(ZArchiveReader.InvalidNode, out _));
    }

    [Fact]
    public void DecodeExtendedNames_ResolvesLongNameEntries()
    {
        var longName = new string('a', 300) + ".bin";
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile(longName));
            w.AppendData([7, 8, 9]);
        });

        // Default: the 0.1.2 quirk hides the entry (byte parity).
        using (var strict = ZArchiveReader.TryOpen(zar))
        {
            Assert.NotNull(strict);
            Assert.Equal(ZArchiveReader.InvalidNode, strict.LookUp(longName));
        }

        using var reader = ZArchiveReader.TryOpen(zar,
            new ZArchiveReaderOptions { DecodeExtendedNames = true });
        Assert.NotNull(reader);
        var node = reader.LookUp(longName);
        Assert.NotEqual(ZArchiveReader.InvalidNode, node);
        Assert.True(reader.TryGetNodeName(node, out var name));
        Assert.Equal(longName, name);
        Assert.Equal([7, 8, 9], reader.ReadFile(node));
    }

    // ------------------------------------------------------------------
    // Entry streams
    // ------------------------------------------------------------------

    [Fact]
    public void OpenRead_StreamsSeeksAndRejectsNonFiles()
    {
        var data = PatternBytes(200000, 7);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("dir"));
            Assert.True(w.StartNewFile("big.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        var stream = reader.TryOpenRead("BIG.BIN");
        Assert.NotNull(stream);
        Assert.Equal(data.Length, stream.Length);

        var all = new byte[data.Length];
        var total = 0;
        while (total < all.Length)
        {
            var read = stream.Read(all, total, all.Length - total);
            Assert.True(read > 0);
            total += read;
        }

        Assert.Equal(data, all);

        stream.Seek(-100, SeekOrigin.End);
        var tail = new byte[100];
        Assert.Equal(100, stream.Read(tail, 0, tail.Length));
        Assert.Equal(data.AsSpan(data.Length - 100).ToArray(), tail);

        Assert.Null(reader.TryOpenRead("missing.bin"));
        Assert.Null(reader.TryOpenRead("dir"));
        Assert.Throws<ArgumentException>(() => reader.OpenRead(reader.LookUp("dir")));
        Assert.Throws<ArgumentException>(() => reader.OpenRead(ZArchiveReader.InvalidNode));

        stream.Dispose();
        Assert.Throws<ObjectDisposedException>(() => stream.Read(tail, 0, 1));
    }

    // ------------------------------------------------------------------
    // Tree metadata and cache sizing
    // ------------------------------------------------------------------

    [Fact]
    public void EntryCountAndTotalSize_MatchTree()
    {
        var a = PatternBytes(1000, 1);
        var b = PatternBytes(70000, 2);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.MakeDir("d"));
            Assert.True(w.StartNewFile("a.bin"));
            w.AppendData(a);
            Assert.True(w.StartNewFile("d/b.bin"));
            w.AppendData(b);
        });

        using var reader = ZArchiveReader.TryOpen(zar)!;
        Assert.Equal((ulong)(a.Length + b.Length), reader.TotalUncompressedSize);
        Assert.Equal(4u, reader.EntryCount); // root, d, a.bin, d/b.bin
        Assert.Equal(2u, reader.GetDirEntryCount(ZArchiveReader.RootNode));
    }

    [Fact]
    public void SmallCache_StillReadsAcrossBlocks()
    {
        var data = PatternBytes(300000, 9);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("big.bin"));
            w.AppendData(data);
        });

        using var reader = ZArchiveReader.TryOpen(zar,
            new ZArchiveReaderOptions { CacheBlockCount = 1 })!;
        var node = reader.LookUp("big.bin");
        Assert.Equal(data, reader.ReadFile(node));

        var one = new byte[1];
        for (var block = 0; block < 5; block++)
        {
            var off = (block * 65536) + 33;
            Assert.Equal(1ul, reader.ReadFromFile(node, (ulong)off, one));
            Assert.Equal(data[off], one[0]);
        }
    }

    [Fact]
    public async Task ParallelReads_DistinctBlocksAreCorrect()
    {
        const int taskCount = 8;
        const int slice = 128 * 1024;
        var data = PatternBytes(taskCount * slice, 11);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("p.bin"));
            w.AppendData(data);
        });

        using (var reader = ZArchiveReader.TryOpen(zar)!)
        {
            await RunParallel(reader);
        }

        // A cache smaller than the working set forces eviction/publish races.
        using (var reader = ZArchiveReader.TryOpen(zar,
                   new ZArchiveReaderOptions { CacheBlockCount = 2 })!)
        {
            await RunParallel(reader);
        }

        return;

        async Task RunParallel(ZArchiveReader reader)
        {
            var node = reader.LookUp("p.bin");
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, taskCount).Select(index => Task.Run(async () =>
            {
                await gate.Task;
                var offset = index * slice;
                var buffer = new byte[slice];
                var read = 0;
                while (read < slice)
                {
                    var n = (int)reader.ReadFromFile(node, (ulong)(offset + read), buffer.AsSpan(read));
                    Assert.True(n > 0);
                    read += n;
                }

                Assert.Equal(data.AsSpan(offset, slice).ToArray(), buffer);
            })).ToArray();

            gate.SetResult();
            await Task.WhenAll(tasks);
        }
    }
}
