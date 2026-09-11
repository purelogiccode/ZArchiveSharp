namespace ZArchiveSharp.Zstd;

/// <summary>
/// Read-only zstd decompression stream: incrementally decodes concatenated
/// frames (plus skippable frames) from the underlying compressed stream and
/// surfaces the decompressed bytes. Block payloads decode through the shared
/// <c>ZstdDecompressor.DecompressBlock</c> path (Huffman/FSE/sequences); this
/// type owns only the incremental framing pump (header parse, block
/// boundaries, checksum verification), mirroring
/// <c>ZstdDecompressor.DecompressFrames</c> checks and messages.
/// <para/>
/// Decoder caps from <see cref="ZstdDecoderOptions"/> are enforced per frame
/// exactly like the single-shot path: frames without a declared content size
/// abort with <see cref="ZstdException"/> past
/// <c>MaxFrameContentSize</c> (zip-bomb guard). A fully served frame releases
/// its buffer, but bytes served before a frame completes are retained until
/// the frame-end checksum verifies — checksum failure therefore throws even
/// if the prefix was already read.
/// With a <see cref="ZstdDictionary"/>, every frame seeds history (and, for
/// formatted dictionaries, initial tables) from it; dictionary bytes are
/// never served.
/// </summary>
public sealed class ZstdDecompressionStream : Stream
{
    private readonly Stream _source;
    private readonly ZstdDecoderOptions _options;
    private readonly ZstdDictionary? _dictionary;
    private readonly bool _leaveOpen;

    // Compressed-input staging: valid bytes are [_inStart, _inEnd).
    private byte[] _inBuf = new byte[8192];
    private int _inStart;
    private int _inEnd;
    private bool _eof;

    // Current frame's decoded bytes plus the served prefix length. Retained
    // until the frame ends (checksum covers the whole frame content).
    private readonly List<byte> _frameOut = [];
    private int _outPos;

    // Null between frames; set while blocks of a frame are being decoded.
    private FrameState? _frame;
    private bool _anyFrame;
    private bool _finished;
    private bool _disposed;

    private sealed class FrameState
    {
        public ZstdDecompressor.FrameContext Ctx = new();
        public bool ChecksumFlag;
        public bool FcsKnown;
        public ulong Fcs;
        public ulong FrameCap;
        public ulong MaxBlock;

        /// <summary>Content start in _frameOut (dictionary size, else 0).</summary>
        public int ServeBase;

        /// <summary>Seeded dictionary history size (0 without a dictionary).</summary>
        public ulong DictSize;
    }

    /// <summary>
    /// Creates a decompression stream over <paramref name="compressed"/>
    /// with default decoder limits.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="compressed"/> is null.</exception>
    public ZstdDecompressionStream(Stream compressed, bool leaveOpen = false)
        : this(compressed, ZstdDecoderOptions.Default, leaveOpen)
    {
    }

    /// <summary>
    /// Creates a decompression stream over <paramref name="compressed"/>
    /// enforcing <paramref name="options"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compressed"/> or <paramref name="options"/> is null.
    /// </exception>
    public ZstdDecompressionStream(Stream compressed, ZstdDecoderOptions options, bool leaveOpen = false)
        : this(compressed, options, null, leaveOpen)
    {
    }

    /// <summary>
    /// Creates a decompression stream over <paramref name="compressed"/>
    /// enforcing <paramref name="options"/> with <paramref name="dict"/>
    /// active on every frame (history plus, for formatted dictionaries,
    /// initial tables). Frames carrying a dictionary ID require it to match
    /// <c>dict.DictId</c>.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compressed"/> or <paramref name="options"/> is null.
    /// </exception>
    public ZstdDecompressionStream(
        Stream compressed, ZstdDecoderOptions options, ZstdDictionary? dict, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        ArgumentNullException.ThrowIfNull(options);

        _source = compressed;
        _options = options;
        _dictionary = dict;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => !_disposed;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => false;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
        {
            throw new ArgumentException("Offset and count exceed buffer size.", nameof(count));
        }

        return Read(new Span<byte>(buffer, offset, count));
    }

    /// <inheritdoc/>
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        if (Serve(buffer, out var served))
        {
            return served;
        }

        if (_finished)
        {
            return 0;
        }

        Pump();
        return Serve(buffer, out served) ? served : 0;
    }

    /// <summary>
    /// Thin-over-sync by design (no I/O threads); still provided for
    /// <c>CopyToAsync</c>. Cancellation is honored before reading.
    /// </summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
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

    /// <summary>
    /// No-op: this stream only reads and holds no unwritten data.
    /// </summary>
    public override void Flush()
    {
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            if (!_leaveOpen)
            {
                _source.Dispose();
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
            if (!_leaveOpen)
            {
                await _source.DisposeAsync().ConfigureAwait(false);
            }

            _disposed = true;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private bool Serve(Span<byte> buffer, out int served)
    {
        var available = _frameOut.Count - _outPos;
        if (available <= 0)
        {
            served = 0;
            return false;
        }

        served = Math.Min(available, buffer.Length);
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frameOut)
            .Slice(_outPos, served).CopyTo(buffer);
        _outPos += served;
        return true;
    }

    // Decodes forward until unserved output exists or the stream end is
    // decided (finished). Only called when no output is pending.
    private void Pump()
    {
        while (_outPos >= _frameOut.Count && !_finished)
        {
            if (_frame is null)
            {
                if (!ParseFrameHeader())
                {
                    return;
                }
            }
            else
            {
                DecodeNextBlock();
            }
        }
    }

    // Parses (or skips) one frame header at the input cursor. Returns false
    // when the underlying stream ended cleanly at a frame boundary.
    private bool ParseFrameHeader()
    {
        if (!EnsureBuffered(4))
        {
            if (_inEnd > _inStart)
            {
                throw new ZstdException("Truncated zstd frame magic.");
            }

            if (!_anyFrame)
            {
                throw new ZstdException("No zstd frame found.");
            }

            _finished = true;
            return false;
        }

        var magic = ZstdDecompressor.ReadU32Le(_inBuf, _inStart);
        if ((magic & ZstdDecompressor.SkippableMask) == ZstdDecompressor.SkippableBase)
        {
            if (!EnsureBuffered(8))
            {
                throw new ZstdException("Truncated skippable frame.");
            }

            var skip = ZstdDecompressor.ReadU32Le(_inBuf, _inStart + 4);
            long need = 8L + skip;
            if (need > int.MaxValue || !EnsureBuffered((int)need))
            {
                throw new ZstdException("Truncated skippable frame.");
            }

            _inStart += 8 + (int)skip;
            return true;
        }

        if (magic != ZstdDecompressor.ZstdMagic)
        {
            throw new ZstdException($"Bad zstd magic 0x{magic:X8}.");
        }

        if (!EnsureBuffered(5))
        {
            throw new ZstdException("Truncated zstd frame header.");
        }

        var descriptor = _inBuf[_inStart + 4];
        if ((descriptor & 0x08) != 0)
        {
            throw new ZstdException("Reserved frame flag set.");
        }

        var fcsFlag = (descriptor >> 6) & 3;
        var singleSegment = (descriptor & 0x20) != 0;
        var checksumFlag = (descriptor & 0x04) != 0;
        var dictFlag = descriptor & 3;

        var pos = _inStart + 5;
        ulong window;
        if (singleSegment)
        {
            window = 0; // filled from FCS below
        }
        else
        {
            if (!EnsureBuffered(pos - _inStart + 1))
            {
                throw new ZstdException("Truncated window descriptor.");
            }

            var wd = _inBuf[pos++];
            var windowLog = 10 + (uint)(wd >> 3);
            var windowBase = 1UL << (int)windowLog;
            var windowAdd = (windowBase / 8) * (uint)(wd & 7);
            window = windowBase + windowAdd;
        }

        if (dictFlag != 0)
        {
            var idSize = dictFlag switch
            {
                1 => 1,
                2 => 2,
                _ => 4
            };
            if (!EnsureBuffered((pos - _inStart) + idSize))
            {
                throw new ZstdException("Truncated zstd frame header.");
            }

            var frameDictId = (uint)ZstdDecompressor.ReadUIntLe(_inBuf, pos, idSize);
            pos += idSize;
            if (_dictionary is null)
            {
                throw new ZstdException(
                    $"{ZstdErrorMessages.DictionaryRequired} (dictID 0x{frameDictId:X8}).");
            }

            if (unchecked((uint)_dictionary.DictId) != frameDictId)
            {
                throw new ZstdException(
                    $"{ZstdErrorMessages.DictionaryMismatch}: frame needs 0x{frameDictId:X8}, dictionary has 0x{_dictionary.DictId:X8}.");
            }
        }

        var fcsSize = fcsFlag switch
        {
            0 => singleSegment ? 1 : 0,
            1 => 2,
            2 => 4,
            _ => 8,
        };

        ulong fcs = 0;
        var fcsKnown = fcsSize != 0;
        if (fcsKnown)
        {
            if (!EnsureBuffered((pos - _inStart) + fcsSize))
            {
                throw new ZstdException("Truncated frame content size.");
            }

            fcs = ZstdDecompressor.ReadUIntLe(_inBuf, pos, fcsSize);
            if (fcsSize == 2)
            {
                fcs += 256;
            }

            pos += fcsSize;
            if (singleSegment)
            {
                window = fcs;
            }
        }

        if (window == 0 && !singleSegment)
        {
            throw new ZstdException("Invalid zstd window size.");
        }

        if (window > _options.MaxWindowSize)
        {
            throw new ZstdException(
                $"zstd window size {window} exceeds decoder limit {_options.MaxWindowSize}.");
        }

        if (fcsKnown && fcs > _options.MaxFrameContentSize)
        {
            throw new ZstdException(
                $"zstd frame content size {fcs} exceeds decoder limit {_options.MaxFrameContentSize}.");
        }

        var maxBlock = window;
        if (maxBlock > (ulong)ZstdDecompressor.MaxBlockSizeLimit)
        {
            maxBlock = (ulong)ZstdDecompressor.MaxBlockSizeLimit;
        }

        _inStart = pos;
        _frameOut.Clear();
        _outPos = 0;
        var serveBase = 0;
        ulong dictSize = 0;
        var ctx = new ZstdDecompressor.FrameContext
        {
            WindowSize = window,
        };
        if (_dictionary is not null)
        {
            if ((ulong)_dictionary.ContentSize > _options.MaxWindowSize)
            {
                throw new ZstdException(
                    $"{ZstdErrorMessages.DictionaryTooLarge}: {_dictionary.ContentSize} content bytes with limit {_options.MaxWindowSize}.");
            }

            dictSize = (ulong)_dictionary.ContentSize;
            serveBase = _dictionary.ContentSize;
            _frameOut.AddRange(_dictionary.Content);
            ctx.LlTable = _dictionary.LlTable;
            ctx.OfTable = _dictionary.OfTable;
            ctx.MlTable = _dictionary.MlTable;
            ctx.HuffmanTable = _dictionary.HuffmanTable;
            for (var i = 0; i < ZstdSeq.RepNum; i++)
            {
                ctx.RepeatOffsets[i] = _dictionary.RepeatOffsets[i];
            }

            _outPos = serveBase;
        }

        _frame = new FrameState
        {
            Ctx = ctx,
            ChecksumFlag = checksumFlag,
            FcsKnown = fcsKnown,
            Fcs = fcs,
            FrameCap = fcsKnown ? fcs : _options.MaxFrameContentSize,
            MaxBlock = maxBlock,
            ServeBase = serveBase,
            DictSize = dictSize,
        };
        _anyFrame = true;
        return true;
    }

    private void DecodeNextBlock()
    {
        var state = _frame!;
        if (!EnsureBuffered(ZstdDecompressor.BlockHeaderSize))
        {
            throw new ZstdException("Truncated block header.");
        }

        var header = (uint)(_inBuf[_inStart]
                            | (_inBuf[_inStart + 1] << 8)
                            | (_inBuf[_inStart + 2] << 16));
        var lastBlock = (header & 1) != 0;
        var blockType = (int)((header >> 1) & 3);
        var blockSize = (int)(header >> 3);
        if ((ulong)blockSize > state.MaxBlock && (blockType == 0 || blockType == 1))
        {
            throw new ZstdException("zstd block too large.");
        }

        switch (blockType)
        {
            case 0: // Raw
                if (!EnsureBuffered(ZstdDecompressor.BlockHeaderSize + blockSize))
                {
                    throw new ZstdException("Truncated raw block.");
                }

                for (var i = 0; i < blockSize; i++)
                {
                    _frameOut.Add(_inBuf[_inStart + ZstdDecompressor.BlockHeaderSize + i]);
                }

                _inStart += ZstdDecompressor.BlockHeaderSize + blockSize;
                break;

            case 1: // RLE
                if (!EnsureBuffered(ZstdDecompressor.BlockHeaderSize + 1))
                {
                    throw new ZstdException("Truncated RLE block.");
                }

                var value = _inBuf[_inStart + ZstdDecompressor.BlockHeaderSize];
                for (var i = 0; i < blockSize; i++)
                {
                    _frameOut.Add(value);
                }

                _inStart += ZstdDecompressor.BlockHeaderSize + 1;
                break;

            case 2: // Compressed
                if (!EnsureBuffered(ZstdDecompressor.BlockHeaderSize + blockSize))
                {
                    throw new ZstdException("Truncated compressed block.");
                }

                ZstdDecompressor.DecompressBlock(
                    _inBuf, _inStart + ZstdDecompressor.BlockHeaderSize, blockSize,
                    _frameOut, state.ServeBase, 0, state.DictSize, state.Ctx, state.MaxBlock);
                _inStart += ZstdDecompressor.BlockHeaderSize + blockSize;
                break;

            default:
                throw new ZstdException("Reserved zstd block type.");
        }

        if ((ulong)(_frameOut.Count - state.ServeBase) > state.FrameCap)
        {
            throw new ZstdException("zstd frame content size mismatch.");
        }

        if (!lastBlock)
        {
            return;
        }

        if (state.ChecksumFlag)
        {
            if (!EnsureBuffered(4))
            {
                throw new ZstdException("Truncated content checksum.");
            }

            var flat = _frameOut.ToArray();
            var actual = (uint)ZstdXxh64.Hash64(flat, state.ServeBase, flat.Length - state.ServeBase);
            var expected = ZstdDecompressor.ReadU32Le(_inBuf, _inStart);
            _inStart += 4;
            if (actual != expected)
            {
                throw new ZstdException("zstd content checksum mismatch.");
            }
        }

        if (state.FcsKnown && (ulong)(_frameOut.Count - state.ServeBase) != state.Fcs)
        {
            throw new ZstdException("zstd frame content size mismatch.");
        }

        _frame = null;
    }

    // Reads from the source until at least minBytes are staged or EOF.
    private bool EnsureBuffered(int minBytes)
    {
        while (_inEnd - _inStart < minBytes && !_eof)
        {
            if (_inEnd == _inBuf.Length)
            {
                if (_inStart > 0)
                {
                    var valid = _inEnd - _inStart;
                    Array.Copy(_inBuf, _inStart, _inBuf, 0, valid);
                    _inStart = 0;
                    _inEnd = valid;
                }
                else
                {
                    var grown = new byte[Math.Max(_inBuf.Length * 2, minBytes)];
                    Array.Copy(_inBuf, grown, _inEnd);
                    _inBuf = grown;
                }
            }

            var read = _source.Read(_inBuf, _inEnd, _inBuf.Length - _inEnd);
            if (read == 0)
            {
                _eof = true;
            }
            else
            {
                _inEnd += read;
            }
        }

        return _inEnd - _inStart >= minBytes;
    }
}