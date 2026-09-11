using ZArchiveSharp.Seekable;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for the §1.3 streaming overloads: <c>ZArchiveWriter.AppendData(Stream)</c>,
/// <c>SeekableWriter.Write(Stream)</c>, and the <c>SeekableReader</c> stream
/// constructors. Pumps must equal equivalent span writes byte-for-byte, and
/// the stream reader must match the byte-array reader without loading frames.
/// </summary>
public sealed class StreamApiTests
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

    private static string WriteTempFile(byte[] data, string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    // Reads at most maxChunk bytes per call, so pump chunking cannot align
    // with frame/block boundaries by construction.
    private sealed class ChunkedStream(byte[] data, int maxChunk) : MemoryStream(data, writable: false)
    {
        private readonly int _maxChunk = maxChunk;

        public override int Read(byte[] buffer, int offset, int count)
        {
            return base.Read(buffer, offset, Math.Min(count, _maxChunk));
        }

        public override int Read(Span<byte> buffer)
        {
            return base.Read(buffer[..Math.Min(buffer.Length, _maxChunk)]);
        }
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    // ------------------------------------------------------------------
    // ZArchiveWriter.AppendData(Stream)
    // ------------------------------------------------------------------

    [Fact]
    public void Writer_AppendDataStream_RoundTripsAcrossBlocks()
    {
        // 200000 bytes cross several 64 KiB writer blocks via the pump.
        var content = PatternBytes(200000, seed: 3);
        var zar = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("f.bin"));
            using var input = new MemoryStream(content, writable: false);
            w.AppendData(input);
        });

        using var reader = ZArchiveReader.TryOpen(zar);
        Assert.NotNull(reader);
        var node = reader.LookUp("f.bin");
        Assert.NotEqual(ZArchiveReader.InvalidNode, node);
        Assert.Equal(content, reader.ReadFile(node));
    }

    [Fact]
    public void Writer_AppendDataStream_MatchesSpanWrites()
    {
        var content = PatternBytes(200000, seed: 3);
        var viaSpan = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("f.bin"));
            w.AppendData(content);
        });
        var viaStream = BuildArchive(w =>
        {
            Assert.True(w.StartNewFile("f.bin"));
            using var input = new ChunkedStream(content, maxChunk: 1000);
            w.AppendData(input);
        });
        Assert.Equal(viaSpan, viaStream);
    }

    [Fact]
    public void Writer_AppendDataStream_Errors()
    {
        using var ms = new MemoryStream();
        using var writer = new ZArchiveWriter(ms);
        Assert.True(writer.StartNewFile("f.bin"));
        Assert.Throws<ArgumentNullException>(() => writer.AppendData((Stream)null!));
        Assert.Throws<ArgumentException>(() => writer.AppendData(new WriteOnlyStream()));
        writer.Finalize();
        using var empty = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => writer.AppendData(empty));
    }

    // ------------------------------------------------------------------
    // SeekableWriter.Write(Stream)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(SeekableFrameSizePolicy.Uncompressed)]
    [InlineData(SeekableFrameSizePolicy.Compressed)]
    public void SeekWriter_WriteStream_FramingRules(SeekableFrameSizePolicy policy)
    {
        var input = PatternBytes(300000, seed: 5);
        var options = new SeekableOptions { Level = 6, FrameSize = 70000, Policy = policy, Checksum = true };

        var single = new SeekableWriter(options);
        single.Write(input);
        var whole = single.Finish();

        var split = new SeekableWriter(options);
        split.Write(input.AsSpan(0, 100000));
        split.Write(input.AsSpan(100000, 131073));
        split.Write(input.AsSpan(231073));
        var splitBytes = split.Finish();

        // Full 128 KiB pump reads take exactly like one span Write.
        var pumpedFull = new SeekableWriter(options);
        using (var full = new MemoryStream(input, writable: false))
        {
            pumpedFull.Write(full);
        }

        var pumpedFullBytes = pumpedFull.Finish();
        Assert.Equal(whole, pumpedFullBytes);

        // Short reads can shift Compressed-policy boundaries (like the
        // oracle's own framing shifting with its input read sizes); every
        // framing stays valid and decodes identically.
        var pumpedChunked = new SeekableWriter(options);
        using (var chunked = new ChunkedStream(input, maxChunk: 1000))
        {
            pumpedChunked.Write(chunked);
        }

        var chunkedBytes = pumpedChunked.Finish();
        Assert.Equal(input, new SeekableReader(chunkedBytes).DecompressAll());
        Assert.Equal(input, new SeekableReader(splitBytes).DecompressAll());

        if (policy == SeekableFrameSizePolicy.Uncompressed)
        {
            // Fixed-size frames drain per take: boundaries never move.
            Assert.Equal(whole, splitBytes);
            Assert.Equal(whole, chunkedBytes);
        }
    }

    [Fact]
    public void SeekWriter_WriteEmptyStream_MatchesEmptyWrite()
    {
        var options = new SeekableOptions { Level = 3, FrameSize = 70000 };
        var expected = new SeekableWriter(options).Finish();

        var pumped = new SeekableWriter(options);
        using var empty = new MemoryStream();
        pumped.Write(empty);
        var actual = pumped.Finish();
        Assert.Equal(expected, actual);

        var reader = new SeekableReader(actual);
        Assert.Equal(1, reader.FrameCount);
        Assert.Empty(reader.DecompressAll());
    }

    [Fact]
    public void SeekWriter_WriteStream_Errors()
    {
        var writer = new SeekableWriter(new SeekableOptions { Level = 3 });
        Assert.Throws<ArgumentNullException>(() => writer.Write((Stream)null!));
        Assert.Throws<ArgumentException>(() => writer.Write(new WriteOnlyStream()));
        writer.Finish();
        using var empty = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => writer.Write(empty));
    }

    // ------------------------------------------------------------------
    // SeekableReader(Stream)
    // ------------------------------------------------------------------

    private static byte[] WriteSeekableFoot(byte[] input)
    {
        var writer = new SeekableWriter(new SeekableOptions { Level = 6, FrameSize = 70000, Checksum = true });
        writer.Write(input);
        return writer.Finish();
    }

    [Fact]
    public void SeekReader_FileStream_MatchesByteArray()
    {
        var input = PatternBytes(300000, seed: 11);
        var file = WriteSeekableFoot(input);
        var path = WriteTempFile(file, "seekstream");
        try
        {
            using var fs = File.OpenRead(path);
            var reader = new SeekableReader(fs);
            Assert.Equal(0, fs.Position);

            var expected = new SeekableReader(file);
            Assert.Equal(expected.FrameCount, reader.FrameCount);
            Assert.Equal(expected.DecompressedLength, reader.DecompressedLength);
            Assert.Equal(input, reader.DecompressAll());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SeekReader_FileStream_RangeAndFrames()
    {
        var input = PatternBytes(300000, seed: 11);
        var file = WriteSeekableFoot(input);
        var path = WriteTempFile(file, "seekrange");
        try
        {
            var expected = new SeekableReader(file);
            using var fs = File.OpenRead(path);
            var reader = new SeekableReader(fs);
            Assert.Equal(expected.DecompressRange(69987, 150000), reader.DecompressRange(69987, 150000));
            Assert.Equal(expected.DecompressFrames(1, 3), reader.DecompressFrames(1, 3));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SeekReader_FileStream_HeadTable()
    {
        var input = PatternBytes(200000, seed: 13);
        var writer = new SeekableWriter(new SeekableOptions { Level = 6, FrameSize = 70000 });
        writer.Write(input);
        var (frames, head) = writer.FinishHead();

        var path = WriteTempFile(frames, "seekhead");
        try
        {
            using var fs = File.OpenRead(path);
            var reader = new SeekableReader(fs, SeekTable.ParseHead(head));
            Assert.Equal(input, reader.DecompressAll());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SeekReader_Stream_Errors()
    {
        var input = PatternBytes(300000, seed: 11);
        var file = WriteSeekableFoot(input);

        Assert.Throws<ArgumentNullException>(() => new SeekableReader((Stream)null!));

        using (var nonSeekable = new NonSeekableStream(new MemoryStream(file, writable: false)))
        {
            Assert.Throws<ArgumentException>(() => new SeekableReader(nonSeekable));
        }

        // Tail cut mid-table: no valid Foot.
        using (var cut = new MemoryStream(file[..^1000], writable: false))
        {
            Assert.Throws<ZstdException>(() => new SeekableReader(cut));
        }

        // Valid table but stream shorter than TotalComp.
        var table = new SeekableReader(file).Table;
        using (var @short = new MemoryStream(file[..^5000], writable: false))
        {
            Assert.Throws<ZstdException>(() => new SeekableReader(@short, table));
        }
    }
}