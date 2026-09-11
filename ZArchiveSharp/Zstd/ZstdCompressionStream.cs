namespace ZArchiveSharp.Zstd;

/// <summary>
/// Write-only zstd compression stream: everything written is buffered and, on
/// <see cref="Stream.Dispose()"/>, emitted as one logical frame with an
/// unknown-size header (FCS-flag-0, row-0 parameters) — byte-identical to
/// <c>ZstdCompressor.EncodeStreamingFrame</c> over the concatenated input,
/// decodable by stock <c>zstd</c> and by <see cref="ZstdDecompressor"/>.
/// With <see cref="ZstdCompressionOptions.Dictionary"/> set, the frame uses
/// the dictionary as history (byte-identical to
/// <c>ZstdCompressor.EncodeDictStreamingFrame</c>); the header is then
/// deferred to <c>Dispose()</c> because its window covers dictionary +
/// content, so <see cref="Stream.Flush"/> only forwards to the destination.
/// <para/>
/// Buffering is deliberate, not lazy: cross-block match state (persistent
/// <c>ZstdFrameState</c>, repeat offsets, entropy reuse, splitter savings)
/// requires the full input for byte-identity with the single-shot path, so
/// block payloads are emitted at finalize time. <see cref="Stream.Flush"/>
/// emits the 6-byte frame header once payload exists, so a flushed prefix is
/// a truncated frame the decoder rejects — never corrupt data.
/// </summary>
public sealed class ZstdCompressionStream : Stream
{
    private readonly Stream _destination;
    private readonly int _level;
    private readonly bool _checksum;
    private readonly ZstdDictionary? _dictionary;
    private readonly bool _leaveOpen;
    private readonly MemoryStream _pending = new();
    private bool _headerWritten;
    private bool _finalized;
    private bool _disposed;

    /// <summary>
    /// Creates a compression stream writing one frame to
    /// <paramref name="destination"/> at <paramref name="level"/> (1..22).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 1..22.</exception>
    public ZstdCompressionStream(Stream destination, int level = 6, bool checksum = false, bool leaveOpen = false)
        : this(destination, ValidatedOptions(level, checksum), leaveOpen)
    {
    }

    /// <summary>
    /// Creates a compression stream writing one frame to
    /// <paramref name="destination"/> with <paramref name="options"/>.
    /// <paramref name="options"/> may carry a <c>Dictionary</c>: the frame
    /// then uses it as history (with its ID field), and the header is
    /// deferred to <c>Dispose()</c> because the window depends on the total
    /// input size.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> or <paramref name="options"/> is null.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><c>options.Level</c> is outside 1..22.</exception>
    public ZstdCompressionStream(Stream destination, ZstdCompressionOptions options, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Level, 22);

        _destination = destination;
        _level = options.Level;
        _checksum = options.ChecksumFlag;
        _dictionary = options.Dictionary;
        _leaveOpen = leaveOpen;
    }

    private static ZstdCompressionOptions ValidatedOptions(int level, bool checksum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        return new ZstdCompressionOptions { Level = level, ChecksumFlag = checksum };
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => !_disposed;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count exceed buffer size.", nameof(count));
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count == 0)
        {
            return;
        }

        _pending.Write(buffer, offset, count);
        EnsureHeader();
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return;
        }

        _pending.Write(buffer);
        EnsureHeader();
    }

    /// <inheritdoc/>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!buffer.IsEmpty)
        {
            // MemoryStream has no span-append without a copy either; ToArray
            // keeps this thin-over-sync as designed (no I/O threads).
            _pending.Write(buffer.Span);
            EnsureHeader();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Emits the frame header if payload has been written but the header has
    /// not gone out yet. Block payloads are emitted at finalize time (see the
    /// class remarks); the flushed prefix therefore decodes only after
    /// <c>Dispose()</c> completes the frame.
    /// </summary>
    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureHeader();
        _destination.Flush();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            FinalizeFrame();
            _pending.Dispose();
            if (!_leaveOpen)
            {
                _destination.Dispose();
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            FinalizeFrame();
            _pending.Dispose();
            if (!_leaveOpen)
            {
                await _destination.DisposeAsync().ConfigureAwait(false);
            }

            _disposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureHeader()
    {
        if (_headerWritten || _pending.Length == 0)
        {
            return;
        }

        if (_dictionary is not null)
        {
            // Dictionary headers carry the window for dictionary + content,
            // which is unknown until finalize: never emit early, so Flush()
            // is a pass-through and the whole frame goes out at Dispose().
            return;
        }

        // Row-0 (unknown-size) parameters, exactly as EncodeStreamingFrame
        // resolves them: the header never depends on the payload.
        var prm = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Default, _level);
        var header = new byte[6];
        var len = ZstdCompressor.WriteStreamingFrameHeader(prm.WindowLog, header, 0, _checksum);
        _destination.Write(header, 0, len);
        _headerWritten = true;
    }

    private void FinalizeFrame()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        var input = _pending.ToArray();
        var frame = _dictionary is null
            ? ZstdCompressor.EncodeStreamingFrame(input, _level, _checksum)
            : ZstdCompressor.EncodeDictStreamingFrame(input, _level, _checksum, _dictionary);
        if (!_headerWritten)
        {
            // Nothing was flushed early (or the input is empty, which takes
            // the pledged-0 single-shot header form): emit the frame whole.
            _destination.Write(frame, 0, frame.Length);
            _headerWritten = true;
            return;
        }

        // The header went out early; skip its 6 bytes. The header is fully
        // determined by (level, checksum), so the prefix must match.
        const int headerLen = 6;
        var header = new byte[headerLen];
        var prm = ZstdCompressionParameters.ForTierLevel(
            ZstdCompressionParameters.SizeTier.Default, _level);
        ZstdCompressor.WriteStreamingFrameHeader(prm.WindowLog, header, 0, _checksum);
        if (frame.Length < headerLen || !frame.AsSpan(0, headerLen).SequenceEqual(header))
        {
            throw new InvalidOperationException("Streaming frame header mismatch (unreachable).");
        }

        _destination.Write(frame, headerLen, frame.Length - headerLen);
    }
}
