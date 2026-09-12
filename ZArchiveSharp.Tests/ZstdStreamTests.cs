using System.Buffers.Binary;
using System.Diagnostics;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Phase 1 stream tests: <see cref="ZstdCompressionStream"/> /
/// <see cref="ZstdDecompressionStream"/> round-trip matrix, chunking
/// invariance, flush/truncation/cap failure paths, single-shot interop, and
/// stock-<c>zstd</c> CLI spot checks (skipped when the tool is absent).
/// Corpus is deterministic (fixed seeds), matching repo convention.
/// </summary>
public sealed class ZstdStreamTests
{
    private static byte[] Text(int n)
    {
        const string sample = "The quick brown fox jumps over the lazy dog. ZArchive block 64 KiB. ";
        var ascii = System.Text.Encoding.ASCII.GetBytes(sample);
        var outBuf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            outBuf[i] = ascii[i % ascii.Length];
        }

        return outBuf;
    }

    private static byte[] Random(int n, int seed)
    {
        var outBuf = new byte[n];
        new Random(seed).NextBytes(outBuf);
        return outBuf;
    }

    private static byte[] Hetero(int n)
    {
        // First half text, second half random: compressible + incompressible
        // blocks in one frame.
        var outBuf = Text(n);
        var rnd = Random(n - (n / 2), 0x5EED2026 + n);
        Array.Copy(rnd, 0, outBuf, n / 2, outBuf.Length - (n / 2));
        return outBuf;
    }

    private static byte[] MakeKind(int size, string kind)
    {
        return kind switch
        {
            "text" => Text(size),
            "random" => Random(size, 3000 + size),
            "hetero" => Hetero(size),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static byte[] CompressViaStream(byte[] input, int level, bool checksum, int chunk)
    {
        using var dest = new MemoryStream();
        using (var enc = new ZstdCompressionStream(dest, level, checksum, leaveOpen: true))
        {
            for (var i = 0; i < input.Length; i += chunk)
            {
                enc.Write(input, i, Math.Min(chunk, input.Length - i));
            }
        }

        return dest.ToArray();
    }

    private static byte[] DecompressViaStream(byte[] frame)
    {
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        return outMs.ToArray();
    }

    public static TheoryData<int, string, int, bool> Matrix()
    {
        var data = new TheoryData<int, string, int, bool>();
        int[] sizes = [8192, 65536, 204800];
        string[] kinds = ["text", "random", "hetero"];
        int[] levels = [1, 6, 19];
        foreach (var s in sizes)
        {
            foreach (var k in kinds)
            {
                foreach (var l in levels)
                {
                    data.Add(s, k, l, false);
                    data.Add(s, k, l, true);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Stream_RoundTrip_Matrix(int size, string kind, int level, bool checksum)
    {
        var input = MakeKind(size, kind);
        var frame = CompressViaStream(input, level, checksum, 65536);
        Assert.Equal(input, DecompressViaStream(frame));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    [InlineData(65536)]
    [InlineData(204800)]
    public void Stream_ChunkingInvariant(int chunk)
    {
        var input = Hetero(204800);
        var baseline = CompressViaStream(input, 6, true, 204800);
        Assert.Equal(baseline, CompressViaStream(input, 6, true, chunk));
    }

    [Fact]
    public void Stream_ChunkingInvariant_SmallLevels()
    {
        var input = Text(8192);
        var baseline = CompressViaStream(input, 1, false, 8192);
        Assert.Equal(baseline, CompressViaStream(input, 1, false, 1));
        var l19 = CompressViaStream(input, 19, true, 8192);
        Assert.Equal(l19, CompressViaStream(input, 19, true, 7));
    }

    [Theory]
    [InlineData(65536, 6, false)]
    [InlineData(65536, 6, true)]
    [InlineData(204800, 6, true)]
    [InlineData(204800, 19, false)]
    public void Stream_BytesEqual_EncodeStreamingFrame(int size, int level, bool checksum)
    {
        // Phase 1 acceptance (2.5): the stream must emit exactly what the
        // single-shot streaming-frame path produces for the same input.
        var input = Hetero(size);
        var expected = ZstdCompressor.EncodeStreamingFrame(input, level, checksum);
        Assert.Equal(expected, CompressViaStream(input, level, checksum, 4096));
        Assert.Equal(expected, CompressViaStream(input, level, checksum, size));
    }

    [Fact]
    public void Stream_OutputDecodesViaSingleShot()
    {
        var input = Hetero(200000);
        var frame = CompressViaStream(input, 6, true, 4096);
        Assert.Equal(input, ZstdCompressor.DecompressFrame(frame, input.Length));
    }

    [Fact]
    public void Stream_DecodesSingleShotFrames()
    {
        // Single-shot frames carry a known-size header (different header
        // form than streaming frames): exercises that parse branch.
        var input = Hetero(70000);
        var frame = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock(input);
        Assert.Equal(input, DecompressViaStream(frame));
    }

    [Fact]
    public void Stream_FlushPrefixDecodesOnlyAfterDispose()
    {
        var input = Hetero(100000);
        using var dest = new MemoryStream();
        var enc = new ZstdCompressionStream(dest, 6, false, leaveOpen: true);
        enc.Write(input, 0, input.Length / 2);
        enc.Flush();
        Assert.True(dest.Length >= 6, "Flush must emit at least the frame header.");
        var prefix = dest.ToArray();
        using (var src = new MemoryStream(prefix, writable: false))
        using (var dec = new ZstdDecompressionStream(src))
        {
            Assert.Throws<ZstdException>(() => dec.CopyTo(Stream.Null));
        }

        enc.Write(input, input.Length / 2, input.Length - (input.Length / 2));
        enc.Dispose();
        Assert.Equal(input, DecompressViaStream(dest.ToArray()));
    }

    [Fact]
    public void Stream_Empty_RoundTrips()
    {
        using var dest = new MemoryStream();
        using (new ZstdCompressionStream(dest, leaveOpen: true))
        {
        }

        var frame = dest.ToArray();
        Assert.Equal([], DecompressViaStream(frame));
        Assert.Equal([], ZstdDecompressor.Decompress(frame));
    }

    [Fact]
    public void Stream_ConcatFrames_DecodeInOrder()
    {
        var first = Text(10000);
        var second = Random(5000, 77);
        var frames = CompressViaStream(first, 6, false, 8192);
        frames = [.. frames, .. CompressViaStream(second, 6, true, 8192)];
        Assert.Equal([.. first, .. second], DecompressViaStream(frames));
    }

    [Fact]
    public void Stream_SkippableFrame_IsSkipped()
    {
        var content = Text(3000);
        var frame = CompressViaStream(content, 6, false, 8192);
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var combined = new List<byte> { 0x50, 0x2A, 0x4D, 0x18 };
        combined.Add((byte)payload.Length);
        combined.Add(0);
        combined.Add(0);
        combined.Add(0);
        combined.AddRange(payload);
        combined.AddRange(frame);
        Assert.Equal(content, DecompressViaStream([.. combined]));
    }

    [Fact]
    public void Stream_FrameHeaderAtFullBufferTail_Decodes()
    {
        // Regression: a frame header can begin with exactly 5 bytes left in a
        // physically full staging buffer. Parsing the window descriptor then
        // compacts the buffer and moves the data to index 0, so the header
        // cursor must be rebased or every following field reads the wrong
        // offset (previously the index ran off the buffer).
        var content = Text(20000);
        var second = CompressViaStream(content, 6, checksum: true, 8192);

        // 8187 skippable bytes leave the next frame's first 5 header bytes at
        // the 8192-byte staging-buffer boundary.
        var payload = new byte[8187 - 8];
        var combined = new List<byte> { 0x50, 0x2A, 0x4D, 0x18, 0, 0, 0, 0 };
        combined[4] = (byte)payload.Length;
        combined[5] = (byte)(payload.Length >> 8);
        combined.AddRange(payload);

        // The regression only exists while the next frame's 5 header bytes
        // land exactly at the staging-buffer boundary; fail loudly if buffer
        // sizing ever drifts instead of silently losing the coverage.
        Assert.Equal(ZstdDecompressionStream.DefaultInputBufferSize, combined.Count + 5);
        combined.AddRange(second);
        Assert.Equal(content, DecompressViaStream([.. combined]));
    }

    [Fact]
    public void Stream_HugeSkippableFrame_SkipsWithoutBuffering()
    {
        // Regression: a skippable frame's declared payload can claim up to
        // 4 GiB. Staging it before detecting truncation allocated that much;
        // a seekable source must be skipped, not read.
        const long gap = 8L * 1024 * 1024;
        var content = Text(4096);
        var frame = CompressViaStream(content, 6, false, 4096);

        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x184D2A50);
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), (uint)gap);
        using var src = new SparseSkippableStream(prefix, gap, frame);
        using var dec = new ZstdDecompressionStream(src);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);

        Assert.Equal(content, outMs.ToArray());
        Assert.True(src.BytesRead < gap / 2,
            $"skippable payload was read ({src.BytesRead} bytes); it must be skipped");
    }

    // Seekable stream: real prefix, a sparse zero gap of a declared length,
    // then real suffix bytes. Counts the bytes actually handed to the caller
    // so tests can prove a skippable payload was seeked over, not read.
    private sealed class SparseSkippableStream(byte[] prefix, long gapLength, byte[] suffix) : Stream
    {
        private readonly long _suffixStart = prefix.Length + gapLength;
        private readonly byte[] _suffix = suffix;
        private readonly byte[] _prefix = prefix;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => _suffixStart + _suffix.Length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = 0;
            while (read < count && Position < Length)
            {
                int take;
                if (Position < _prefix.Length)
                {
                    take = (int)Math.Min(count - read, _prefix.Length - Position);
                    Array.Copy(_prefix, Position, buffer, offset + read, take);
                }
                else if (Position < _suffixStart)
                {
                    take = (int)Math.Min(count - read, _suffixStart - Position);
                    Array.Clear(buffer, offset + read, take);
                }
                else
                {
                    take = (int)Math.Min(count - read, Length - Position);
                    Array.Copy(_suffix, Position - _suffixStart, buffer, offset + read, take);
                }

                Position += take;
                read += take;
            }

            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => Position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            if (target < 0)
            {
                throw new IOException("Attempted to seek before the start of the stream.");
            }

            Position = target;
            return Position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Stream_Truncated_Throws()
    {
        var frame = CompressViaStream(Hetero(100000), 6, true, 8192);
        var cut = frame[..(frame.Length / 2)];
        using var src = new MemoryStream(cut, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        var buf = new byte[4096];
        Assert.Throws<ZstdException>(() =>
        {
            while (dec.Read(buf, 0, buf.Length) != 0)
            {
            }
        });
    }

    [Fact]
    public void Stream_EmptyInput_ThrowsNoFrame()
    {
        using var src = new MemoryStream([], writable: false);
        using var dec = new ZstdDecompressionStream(src);
        Assert.Throws<ZstdException>(() => dec.ReadByte());
    }

    [Fact]
    public void Stream_ReadPastEnd_ReturnsZero()
    {
        var frame = CompressViaStream(Text(1000), 6, false, 1000);
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        Assert.Equal(0, dec.Read(new byte[16], 0, 16));
        Assert.Equal(0, dec.Read(new Span<byte>(new byte[16])));
        Assert.Equal(-1, dec.ReadByte());
    }

    [Fact]
    public void Stream_CapExceeded_Throws()
    {
        var frame = CompressViaStream(Text(8192), 1, false, 8192);
        var options = new ZstdDecoderOptions { MaxFrameContentSize = 64 };
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src, options);
        Assert.Throws<ZstdException>(() => dec.CopyTo(Stream.Null));
    }

    [Fact]
    public void Stream_FullyServedFrame_ReleasesBuffer()
    {
        var input = Hetero(200000);
        var frame = CompressViaStream(input, 6, true, 5000);
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        Assert.Equal(input, outMs.ToArray());

        // Pre-fix the decoded frame stayed referenced until the next header
        // parsed (or dispose): a fully served frame must release it eagerly.
        Assert.Equal(0, dec.RetainedFrameBytes);
    }

    [Fact]
    public void Stream_ChecksumMismatch_Throws()
    {
        // Random data encodes as raw blocks (no other validation), so a
        // flipped payload byte isolates the checksum check.
        var frame = CompressViaStream(Random(8192, 9182), 1, true, 8192);
        frame[20] ^= 0xFF;
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        Assert.Throws<ZstdException>(() => dec.CopyTo(Stream.Null));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(65536)]
    public void Stream_ReadChunkSizes_Agree(int readChunk)
    {
        var input = Hetero(100000);
        var frame = CompressViaStream(input, 6, true, 5000);
        using var src = new MemoryStream(frame, writable: false);
        using var dec = new ZstdDecompressionStream(src);
        using var outMs = new MemoryStream();
        var buf = new byte[readChunk];
        int n;
        while ((n = dec.Read(buf, 0, buf.Length)) != 0)
        {
            outMs.Write(buf, 0, n);
        }

        Assert.Equal(input, outMs.ToArray());
    }

    [Fact]
    public void Stream_WriteByte_ReadByte_RoundTrip()
    {
        var input = Text(5000);
        using var dest = new MemoryStream();
        using (var enc = new ZstdCompressionStream(dest, leaveOpen: true))
        {
            foreach (var b in input)
            {
                enc.WriteByte(b);
            }
        }

        using var src = new MemoryStream(dest.ToArray(), writable: false);
        using var dec = new ZstdDecompressionStream(src);
        var got = new List<byte>();
        int b2;
        while ((b2 = dec.ReadByte()) != -1)
        {
            got.Add((byte)b2);
        }

        Assert.Equal(input, got.ToArray());
    }

    [Fact]
    public async Task Stream_Async_RoundTrip()
    {
        var input = Hetero(100000);
        await using var dest = new MemoryStream();
        await using (var enc = new ZstdCompressionStream(dest, 6, true, leaveOpen: true))
        {
            await enc.WriteAsync(input.AsMemory(0, 40000));
            await enc.WriteAsync(input.AsMemory(40000));
            await enc.FlushAsync();
        }

        await using var src = new MemoryStream(dest.ToArray(), writable: false);
        await using var dec = new ZstdDecompressionStream(src);
        await using var outMs = new MemoryStream();
        await dec.CopyToAsync(outMs);
        Assert.Equal(input, outMs.ToArray());
    }

    [Fact]
    public async Task Stream_ReadAsync_Canceled_Throws()
    {
        var frame = CompressViaStream(Text(100), 1, false, 100);
        await using var src = new MemoryStream(frame, writable: false);
        await using var dec = new ZstdDecompressionStream(src);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            dec.ReadAsync(new Memory<byte>(new byte[16]), cts.Token).AsTask());
    }

    [Fact]
    public void Stream_OptionsCtor_RoundTrips()
    {
        var input = Hetero(70000);
        using var dest = new MemoryStream();
        using (var enc = new ZstdCompressionStream(
                   dest, new ZstdCompressionOptions { Level = 12, ChecksumFlag = true }, leaveOpen: true))
        {
            enc.Write(input);
        }

        Assert.Equal(input, DecompressViaStream(dest.ToArray()));
    }

    [Fact]
    public void Stream_LeaveOpen_True_KeepsBaseUsable()
    {
        using var dest = new MemoryStream();
        using (var enc = new ZstdCompressionStream(dest, leaveOpen: true))
        {
            enc.Write(Text(100));
        }

        Assert.True(dest.CanWrite);
        dest.WriteByte(0);

        using var src = new MemoryStream(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, writable: false);
        using (new ZstdDecompressionStream(src, leaveOpen: true))
        {
        }

        Assert.True(src.CanRead);
    }

    [Fact]
    public void Stream_LeaveOpen_False_DisposesBase()
    {
        var dest = new MemoryStream();
        using (var enc = new ZstdCompressionStream(dest, 1, false, leaveOpen: false))
        {
            enc.Write(Text(100));
        }

        Assert.Throws<ObjectDisposedException>(() => dest.WriteByte(0));

        var src = new MemoryStream(CompressViaStream(Text(100), 1, false, 100), writable: false);
        using (new ZstdDecompressionStream(src, leaveOpen: false))
        {
        }

        Assert.Throws<ObjectDisposedException>(() => src.ReadByte());
    }

    [Fact]
    public void CompressionStream_InvalidUse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ZstdCompressionStream(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ZstdCompressionStream(new MemoryStream(), (ZstdCompressionOptions)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionStream(new MemoryStream(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZstdCompressionStream(new MemoryStream(), 23));
        Assert.Throws<ArgumentException>(() =>
            new ZstdCompressionStream(new MemoryStream()).Write(new byte[4], 3, 2));

        using var dest = new MemoryStream();
        var enc = new ZstdCompressionStream(dest, leaveOpen: true);
        enc.Write(Text(10));
        enc.Dispose();
        Assert.Throws<ObjectDisposedException>(() => enc.Write(Text(10)));
        Assert.Throws<ObjectDisposedException>(() => enc.Write(new byte[1], 0, 1));

        using var enc2 = new ZstdCompressionStream(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => enc2.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => enc2.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => enc2.SetLength(0));
        Assert.Throws<NotSupportedException>(() => _ = enc2.Length);
        Assert.Throws<NotSupportedException>(() => _ = enc2.Position);
        Assert.Throws<NotSupportedException>(() => enc2.Position = 0);
        Assert.False(enc2.CanRead);
        Assert.False(enc2.CanSeek);
    }

    [Fact]
    public void DecompressionStream_InvalidUse_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ZstdDecompressionStream(null!));
        Assert.Throws<ArgumentNullException>(() =>
            new ZstdDecompressionStream(new MemoryStream(), (ZstdDecoderOptions)null!));

        using var dec = new ZstdDecompressionStream(new MemoryStream());
        Assert.Throws<NotSupportedException>(() => dec.Write(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => dec.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => dec.SetLength(0));
        Assert.Throws<NotSupportedException>(() => _ = dec.Length);
        Assert.Throws<NotSupportedException>(() => _ = dec.Position);
        Assert.Throws<NotSupportedException>(() => dec.Position = 0);
        Assert.False(dec.CanWrite);
        dec.Flush(); // no-op, must not throw
    }

    [Fact]
    public void Stream_StockZstd_Interop()
    {
        var zstd = FindZstd();
        if (zstd is null)
        {
            return; // tool absent: covered by single-shot interop tests above
        }

        var work = Path.Combine(Path.GetTempPath(), "zarsharp", "zstd_stream_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var input = Hetero(200000);
            File.WriteAllBytes(Path.Combine(work, "in.bin"), input);

            // Ours decodes under stock zstd.
            var ours = CompressViaStream(input, 6, true, 8192);
            File.WriteAllBytes(Path.Combine(work, "ours.zst"), ours);
            RunZstd(zstd, $"-d -c \"{Path.Combine(work, "ours.zst")}\"", Path.Combine(work, "ours.out"));
            Assert.Equal(input, File.ReadAllBytes(Path.Combine(work, "ours.out")));

            // Stock zstd output decodes through our stream.
            RunZstd(zstd, $"-6 --check -c \"{Path.Combine(work, "in.bin")}\"", Path.Combine(work, "native.zst"));
            Assert.Equal(input, DecompressViaStream(File.ReadAllBytes(Path.Combine(work, "native.zst"))));
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static string? FindZstd()
    {
        try
        {
            var probe = Process.Start(new ProcessStartInfo("zstd", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (probe is null)
            {
                return null;
            }

            probe.WaitForExit(15_000);
            return probe.ExitCode == 0 ? "zstd" : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RunZstd(string zstd, string args, string outPath)
    {
        var psi = new ProcessStartInfo(zstd, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.BaseStream;
        using var outFile = File.Create(outPath);
        stdout.CopyTo(outFile);
        var stderr = proc.StandardError.ReadToEnd();
        var exited = proc.WaitForExit(120_000);
        Assert.True(exited, "zstd timed out.\n" + stderr);
        Assert.True(proc.ExitCode == 0, $"zstd failed.\n{stderr}");
    }

    private sealed class FailOnDemandStream : MemoryStream
    {
        public bool Fail { get; set; }

        public bool Disposed { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Fail)
            {
                throw new IOException("destination failed");
            }

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Fail)
            {
                throw new IOException("destination failed");
            }

            base.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void CompressionStream_FinalizeFailure_StillDisposesDestination()
    {
        // An encode/write failure during Dispose must not leak the
        // destination (leaveOpen: false) or the pending buffer.
        var destination = new FailOnDemandStream();
        var encoder = new ZstdCompressionStream(destination, level: 3, checksum: false, leaveOpen: false);
        encoder.Write(Text(4096)); // emits the frame header, kept in _pending afterwards
        destination.Fail = true;
        Assert.Throws<IOException>(() => encoder.Dispose());
        Assert.True(destination.Disposed, "The destination was left open after a finalization failure.");
    }
}