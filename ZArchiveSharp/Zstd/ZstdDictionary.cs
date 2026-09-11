namespace ZArchiveSharp.Zstd;

/// <summary>
/// An immutable, thread-safe reusable zstd dictionary for dictionary-use
/// compression and decompression (training is out of scope for v1).
/// <para/>
/// Two shapes, mirroring <c>ZSTD_decompress_insertDictionary</c>
/// (<c>lib/decompress/zstd_decompress.c</c>): a <em>formatted</em> dictionary
/// (magic <c>0xEC30A437</c>, dictionary ID, entropy tables, content) parsed
/// into initial decoder tables plus history content, and a <em>raw
/// prefix</em> (arbitrary bytes) used as history only. A supplied dictionary
/// is always active on decode (history, and for formatted dictionaries
/// tables, seed every frame); a frame carrying a dictionary ID additionally
/// requires an ID match, exactly like stock
/// <c>ZSTD_decompress_usingDict</c>.
/// </summary>
public sealed class ZstdDictionary
{
    /// <summary>Dictionary magic (<c>ZSTD_MAGIC_DICTIONARY</c>), little-endian.</summary>
    internal const uint DictionaryMagic = 0xEC30A437;

    // FSE table-log caps for dictionary entropy tables (OffFSELog,
    // MLFSELog, LLFSELog in lib/common/zstd_internal.h).
    private const int MaxOffLog = 8;
    private const int MaxMlLog = 9;
    private const int MaxLlLog = 9;

    private ZstdDictionary(
        byte[] content, int dictId, bool formatted,
        ZstdDecompressor.SeqTable? llTable, ZstdDecompressor.SeqTable? ofTable,
        ZstdDecompressor.SeqTable? mlTable, ZstdHuffman.HuffmanTable? huffmanTable,
        uint[] repeatOffsets)
    {
        Content = content;
        DictId = dictId;
        IsFormatted = formatted;
        LlTable = llTable;
        OfTable = ofTable;
        MlTable = mlTable;
        HuffmanTable = huffmanTable;
        RepeatOffsets = repeatOffsets;
    }

    /// <summary>
    /// Loads a dictionary from <paramref name="dict"/>, auto-detecting the
    /// shape like stock: 8+ bytes starting with magic <c>0xEC30A437</c>
    /// parse as formatted, anything else loads as a raw prefix with ID 0.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="dict"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="dict"/> is empty.</exception>
    /// <exception cref="ZstdException">Formatted bytes fail validation.</exception>
    public static ZstdDictionary FromBytes(byte[] dict)
    {
        ArgumentNullException.ThrowIfNull(dict);
        if (dict.Length == 0)
        {
            throw new ArgumentException("Dictionary must not be empty.", nameof(dict));
        }

        if (dict.Length >= 8 && ReadU32Le(dict, 0) == DictionaryMagic)
        {
            return FromFormatted((byte[])dict.Clone());
        }

        var content = new byte[dict.Length];
        Array.Copy(dict, content, dict.Length);
        return new ZstdDictionary(content, 0, false, null, null, null, null, [1, 4, 8]);
    }

    /// <summary>
    /// Uses <paramref name="prefix"/> as raw history content (prefix mode,
    /// like <c>ZSTD_CCtx_refPrefix</c>). <paramref name="dictId"/> tags the
    /// frames this dictionary writes (0 writes no dictionary-ID field);
    /// decoding a tagged frame requires the same ID.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is empty.</exception>
    public static ZstdDictionary FromRawPrefix(ReadOnlySpan<byte> prefix, uint dictId = 0)
    {
        if (prefix.IsEmpty)
        {
            throw new ArgumentException("Dictionary prefix must not be empty.", nameof(prefix));
        }

        return new ZstdDictionary(
            prefix.ToArray(), unchecked((int)dictId), false, null, null, null, null, [1, 4, 8]);
    }

    /// <summary>
    /// Dictionary ID the frames using this dictionary carry (0 = no
    /// dictionary-ID field; the dictionary still seeds history and, when
    /// formatted, tables). Compared as a 32-bit pattern.
    /// </summary>
    public int DictId { get; }

    /// <summary>True when loaded from formatted (magic-headed) bytes.</summary>
    public bool IsFormatted { get; }

    /// <summary>History content size in bytes.</summary>
    public int ContentSize => Content.Length;

    /// <summary>History content (private copy; never mutated after load).</summary>
    internal byte[] Content { get; }

    /// <summary>Initial literal-length table (formatted dictionaries only).</summary>
    internal ZstdDecompressor.SeqTable? LlTable { get; }

    /// <summary>Initial offset table (formatted dictionaries only).</summary>
    internal ZstdDecompressor.SeqTable? OfTable { get; }

    /// <summary>Initial match-length table (formatted dictionaries only).</summary>
    internal ZstdDecompressor.SeqTable? MlTable { get; }

    /// <summary>Initial Huffman table for treeless literals (formatted only).</summary>
    internal ZstdHuffman.HuffmanTable? HuffmanTable { get; }

    /// <summary>Initial three-entry repeat-offset history (always length 3).</summary>
    internal uint[] RepeatOffsets { get; }

    // Exact port of the ZSTD_loadDEntropy walk (magic + ID already
    // consumed): Huffman table, OF / ML / LL FSE tables, three repeat
    // offsets, then content. Takes ownership of buf.
    private static ZstdDictionary FromFormatted(byte[] buf)
    {
        try
        {
            var dictId = ReadU32Le(buf, 4);
            var pos = 8;

            var huffmanConsumed = ZstdHuffman.ReadStats(
                buf, pos, buf.Length - pos,
                out var weights, out var tableLog, out var numSymbols);
            var huffman = ZstdHuffman.BuildTable(weights, numSymbols, tableLog);
            pos += huffmanConsumed;

            pos = ReadTable(buf, pos, ZstdDecompressor.MaxOff, MaxOffLog, isOffset: true, isMatchLength: false,
                out var ofTable);
            pos = ReadTable(buf, pos, ZstdDecompressor.MaxMl, MaxMlLog, isOffset: false, isMatchLength: true,
                out var mlTable);
            pos = ReadTable(buf, pos, ZstdDecompressor.MaxLl, MaxLlLog, isOffset: false, isMatchLength: false,
                out var llTable);

            if (pos + 12 > buf.Length)
            {
                throw new ZstdException($"{ZstdErrorMessages.DictionaryCorrupted}: truncated repeat offsets.");
            }

            var contentSize = buf.Length - pos - 12;
            if (contentSize <= 0)
            {
                throw new ZstdException($"{ZstdErrorMessages.DictionaryCorrupted}: no content.");
            }

            var rep = new uint[3];
            for (var i = 0; i < 3; i++)
            {
                var r = ReadU32Le(buf, pos);
                pos += 4;
                if (r == 0 || r > (uint)contentSize)
                {
                    throw new ZstdException(
                        $"{ZstdErrorMessages.DictionaryCorrupted}: bad repeat offset {r} for {contentSize} content bytes.");
                }

                rep[i] = r;
            }

            var content = new byte[contentSize];
            Array.Copy(buf, pos, content, 0, contentSize);
            return new ZstdDictionary(
                content, unchecked((int)dictId), true, llTable, ofTable, mlTable, huffman, rep);
        }
        catch (ZstdException ex) when (!ex.Message.StartsWith(ZstdErrorMessages.DictionaryCorrupted,
                                           StringComparison.Ordinal))
        {
            throw new ZstdException($"{ZstdErrorMessages.DictionaryCorrupted}: {ex.Message}", ex);
        }
    }

    private static int ReadTable(
        byte[] buf, int pos, int maxSymbol, int maxLog, bool isOffset, bool isMatchLength,
        out ZstdDecompressor.SeqTable table)
    {
        var consumed = ZstdFse.ParseNormalizedCounts(
            buf, pos, buf.Length - pos, maxSymbol,
            out var norms, out var tableLog, out var maxSym);
        if (tableLog > maxLog)
        {
            throw new ZstdException(
                $"{ZstdErrorMessages.DictionaryCorrupted}: table log {tableLog} exceeds {maxLog}.");
        }

        table = isOffset
            ? ZstdDecompressor.BuildOfTable(norms, maxSym, tableLog)
            : ZstdDecompressor.BuildSeqTable(norms, maxSym, tableLog, isMatchLength);
        return pos + consumed;
    }

    private static uint ReadU32Le(byte[] buf, int offset)
    {
        return (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));
    }
}