using System.Buffers;

namespace ZArchiveSharp.Zstd;

/// <summary>
/// Per-frame matchfinder state for multi-block frames: the full input plus
/// the match tables that persist across the frame's 128 KiB blocks, exactly
/// like <c>ZSTD_MatchState_t</c> (single-shot, contiguous; with a dictionary
/// the frame copy is [dictionary | content] and blocks start at the content
/// offset, like a loaded prefix).
/// Positions are absolute frame offsets; each block parses
/// <c>[blockStart, blockEnd)</c> with its anchor reset to
/// <c>blockStart</c> while matches may reference any earlier frame data
/// within the window. Tables are allocated once from the frame-level
/// (total-size, adjusted) row and start zeroed, so the first block parses
/// exactly like a fresh single-shot block.
/// <para/>
/// Scope note: the window never slides in practice here (inputs are far
/// below the adjusted window size, so the lowest valid match index stays 0
/// and overflow correction cannot trigger); the engines therefore keep their
/// validated zero-based bounds, which are already absolute-safe. Larger
/// inputs would need an explicit low-limit plus overflow correction.
/// </summary>
internal sealed class ZstdFrameState
{
    private readonly byte[] _frame;
    private readonly int _length;

    private uint[]? _fastHash;
    private uint[]? _dfastLong;
    private uint[]? _dfastSmall;
    private uint[]? _lazyHash;
    private uint[]? _lazyChain;
    private byte[]? _lazyTag;
    private ZstdOpt.OptStats? _optStats;
    private uint[]? _optHash;
    private uint[]? _optBt;
    private uint[]? _optHash3;
    private ZstdEntropyState? _stagedEntropy;

    /// <summary>Creates frame state over a private copy of the input.</summary>
    /// <param name="frame">Pooled backing array (may exceed <paramref name="length"/>).</param>
    /// <param name="length">Logical frame length; only [0, length) is readable.</param>
    /// <param name="level">Compression level (1..22).</param>
    /// <param name="prm">Frame-level parameter row.</param>
    public ZstdFrameState(byte[] frame, int length, int level, ZstdCompressionParameters prm)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, frame.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 22);
        _frame = frame;
        _length = length;
        Level = level;
        Prm = prm;
    }

    /// <summary>Creates frame state over an exactly-sized copy of the input.</summary>
    public ZstdFrameState(byte[] frame, int level, ZstdCompressionParameters prm)
        : this(frame, frame?.Length ?? 0, level, prm)
    {
    }

    /// <summary>Compression level (1..22).</summary>
    public int Level { get; }

    /// <summary>Frame-level (total-size, adjusted) parameter row.</summary>
    public ZstdCompressionParameters Prm { get; }

    /// <summary>
    /// Table-update cursor (<c>ms->nextToUpdate</c>), absolute frame offset.
    /// Persists across blocks; passed by reference into the search routines.
    /// </summary>
    public int NextToUpdate;

    /// <summary>Full frame bytes (absolute indexing, logical length only).</summary>
    public ReadOnlySpan<byte> Frame => new(_frame, 0, _length);

    /// <summary>
    /// Parses <c>[blockStart, blockEnd)</c> into <paramref name="store"/>,
    /// updating <paramref name="repeatOffsets"/> per the native end-of-block
    /// rule. All strategies have a stateful port (fast, double-fast,
    /// greedy/lazy family, optimal parsers); the optimal parsers additionally
    /// persist their price statistics (<c>ms-&gt;opt</c>: first block
    /// initializes from its own bytes, later blocks scale down).
    /// </summary>
    public int FindMatches(int blockStart, int blockEnd, ZstdSequenceStore store, uint[] repeatOffsets)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repeatOffsets);
        if (blockEnd < blockStart || blockStart < 0 || blockEnd > _length)
        {
            throw new ArgumentOutOfRangeException(nameof(blockStart));
        }

        if (blockEnd == blockStart)
        {
            store.SetTrailingLiterals([]);
            return 0;
        }

        return Prm.Strategy switch
        {
            ZstdStrategy.Fast => ZstdFast.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.DoubleFast => ZstdDoubleFast.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.Greedy or ZstdStrategy.Lazy or ZstdStrategy.Lazy2 or ZstdStrategy.BtLazy2 =>
                ZstdLazyEngine.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            ZstdStrategy.BtOpt or ZstdStrategy.BtUltra or ZstdStrategy.BtUltra2 =>
                ZstdOpt.FindMatches(this, blockStart, blockEnd, store, repeatOffsets),
            _ => throw new NotSupportedException($"No stateful port for strategy {Prm.Strategy}."),
        };
    }

    /// <summary>Persistent fast hash table (<c>1 &lt;&lt; hashLog</c>, zeroed).</summary>
    internal uint[] FastHashTable()
    {
        if (_fastHash is null)
        {
            _fastHash = ArrayPool<uint>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_fastHash, 0, 1 << Prm.HashLog);
        }

        return _fastHash;
    }

    /// <summary>Persistent double-fast tables (long: <c>hashLog</c>, small: <c>chainLog</c>).</summary>
    internal (uint[] Long, uint[] Small) DoubleFastTables()
    {
        if (_dfastLong is null)
        {
            _dfastLong = ArrayPool<uint>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_dfastLong, 0, 1 << Prm.HashLog);
        }

        if (_dfastSmall is null)
        {
            _dfastSmall = ArrayPool<uint>.Shared.Rent(1 << Prm.ChainLog);
            Array.Clear(_dfastSmall, 0, 1 << Prm.ChainLog);
        }

        return (_dfastLong, _dfastSmall);
    }

    /// <summary>Persistent lazy hash + chain tables (hash-chain and BT searches).</summary>
    internal (uint[] Hash, uint[] Chain) LazyChainTables()
    {
        if (_lazyHash is null)
        {
            _lazyHash = ArrayPool<uint>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_lazyHash, 0, 1 << Prm.HashLog);
        }

        if (_lazyChain is null)
        {
            _lazyChain = ArrayPool<uint>.Shared.Rent(1 << Prm.ChainLog);
            Array.Clear(_lazyChain, 0, 1 << Prm.ChainLog);
        }

        return (_lazyHash, _lazyChain);
    }

    /// <summary>Persistent lazy hash + tag tables (row search).</summary>
    internal (uint[] Hash, byte[] Tag) LazyRowTables()
    {
        if (_lazyHash is null)
        {
            _lazyHash = ArrayPool<uint>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_lazyHash, 0, 1 << Prm.HashLog);
        }

        if (_lazyTag is null)
        {
            _lazyTag = ArrayPool<byte>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_lazyTag, 0, 1 << Prm.HashLog);
        }

        return (_lazyHash, _lazyTag);
    }

    /// <summary>
    /// Frame-persistent block entropy tables (M3:
    /// <c>prevCBlock-&gt;entropy</c>). Starts with every reuse mode at none.
    /// </summary>
    internal ZstdEntropyState Entropy { get; } = new();

    /// <summary>
    /// Stages the next entropy state built for the current block (M3:
    /// <c>nextCBlock-&gt;entropy</c>). The writer confirms or drops it per
    /// the block's fate below.
    /// </summary>
    internal void StageEntropy(ZstdEntropyState next)
    {
        _stagedEntropy = next;
    }

    /// <summary>
    /// Confirms the staged entropy state after emitting a compressed block,
    /// then applies the offset-code valid→check downgrade native applies to
    /// every block (<c>ZSTD_compressBlock_internal</c> tail).
    /// </summary>
    internal void ConfirmEntropy()
    {
        if (_stagedEntropy is not null)
        {
            Entropy.CopyFrom(_stagedEntropy);
            _stagedEntropy = null;
        }

        DowngradeOffcode();
    }

    /// <summary>
    /// Drops the staged entropy state after a raw/RLE/tiny block, keeping
    /// only the offset-code valid→check downgrade native still applies.
    /// </summary>
    internal void DeclineEntropy()
    {
        _stagedEntropy = null;
        DowngradeOffcode();
    }

    private void DowngradeOffcode()
    {
        if (Entropy.OfRepeat == ZstdFseRepeat.Valid)
        {
            Entropy.OfRepeat = ZstdFseRepeat.Check;
        }
    }

    /// <summary>
    /// Persistent optimal-parser price statistics (<c>ms-&gt;opt</c> frequency
    /// half). <c>LitLengthSum == 0</c> detects the first block, exactly like
    /// <c>ZSTD_rescaleFreqs</c>.
    /// </summary>
    internal ZstdOpt.OptStats OptStats()
    {
        return _optStats ??= new ZstdOpt.OptStats();
    }

    /// <summary>
    /// Persistent optimal-parser binary-tree tables (hash, chain, and the
    /// 3-byte table when <c>minMatch</c> is 3).
    /// </summary>
    internal (uint[] Hash, uint[] Bt, uint[] Hash3) OptTables()
    {
        if (_optHash is null)
        {
            _optHash = ArrayPool<uint>.Shared.Rent(1 << Prm.HashLog);
            Array.Clear(_optHash, 0, 1 << Prm.HashLog);
        }

        if (_optBt is null)
        {
            _optBt = ArrayPool<uint>.Shared.Rent(1 << Prm.ChainLog);
            Array.Clear(_optBt, 0, 1 << Prm.ChainLog);
        }

        if (_optHash3 is null)
        {
            var hashLog3 = ZstdOpt.HashLog3For(Prm);
            _optHash3 = hashLog3 > 0 ? ArrayPool<uint>.Shared.Rent(1 << hashLog3) : [];
            if (_optHash3.Length != 0)
            {
                Array.Clear(_optHash3, 0, 1 << hashLog3);
            }
        }

        return (_optHash, _optBt, _optHash3);
    }

    /// <summary>
    /// Returns all rented match tables to the pool. Called once the frame is
    /// fully encoded (see <c>EncodeFrameCore</c>); tables are re-rented (and
    /// re-cleared) on next use, so reuse is invisible to the search. States
    /// that escape without release simply let the GC reclaim the arrays.
    /// </summary>
    internal void ReleaseTables()
    {
        Return(ref _fastHash);
        Return(ref _dfastLong);
        Return(ref _dfastSmall);
        Return(ref _lazyHash);
        Return(ref _lazyChain);
        Return(ref _lazyTag);
        Return(ref _optHash);
        Return(ref _optBt);
        Return(ref _optHash3);
        return;

        static void Return<T>(ref T[]? table)
        {
            if (table is not null)
            {
                // The empty hash3 marker was never rented.
                if (table.Length != 0)
                {
                    ArrayPool<T>.Shared.Return(table);
                }

                table = null;
            }
        }
    }
}