using System.Diagnostics;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Dictionary-use tests (Phase 2): hand-crafted dict frames pin the D-1/D-2
/// decode contract without depending on the encoder; round-trip, stock
/// interop, goldens, and fuzz follow.
/// </summary>
public sealed class ZstdDictTests
{
    // Hand-built dict frame: ID 7 (1-byte field), window 1024, no FCS, one
    // raw last block "hello". Exercises dictID parse + history plumbing
    // with no matches involved.
    private static byte[] RawBlockDictFrame(byte dictIdByte)
    {
        return
        [
            0x28, 0xB5, 0x2F, 0xFD, // magic
            0x01, // descriptor: dictFlag=1, no checksum, multi-segment, no FCS
            0x00, // window descriptor: windowLog 10
            dictIdByte, // dictionary ID
            0x29, 0x00, 0x00, // raw last block, 5 bytes
            0x68, 0x65, 0x6C, 0x6C, 0x6F, // "hello"
        ];
    }

    private static byte[] RawBlockDictFrame2Byte(uint dictId)
    {
        return
        [
            0x28, 0xB5, 0x2F, 0xFD,
            0x02, // descriptor: dictFlag=2
            0x00,
            (byte)dictId, (byte)(dictId >> 8),
            0x29, 0x00, 0x00,
            0x68, 0x65, 0x6C, 0x6C, 0x6F,
        ];
    }

    [Fact]
    public void Decode_RawBlockDictFrame_WithMatchingRawDict()
    {
        var dict = ZstdDictionary.FromRawPrefix("0123456789ABCDEF"u8, dictId: 7);
        Assert.Equal(7, dict.DictId);
        Assert.False(dict.IsFormatted);
        Assert.Equal(16, dict.ContentSize);

        var decoded = ZstdDecompressor.Decompress(RawBlockDictFrame(7), dict);
        Assert.Equal("hello"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_RawBlockDictFrame_With2ByteId()
    {
        var dict = ZstdDictionary.FromRawPrefix("0123456789ABCDEF"u8, dictId: 0x1234);
        var decoded = ZstdDecompressor.Decompress(RawBlockDictFrame2Byte(0x1234), dict);
        Assert.Equal("hello"u8.ToArray(), decoded);
    }

    [Fact]
    public void Decode_DictFrame_WithoutDict_ThrowsRequired()
    {
        var ex = Assert.Throws<ZstdException>(() =>
            ZstdDecompressor.Decompress(RawBlockDictFrame(7)));
        Assert.Contains("requires a dictionary", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0x00000007", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_DictFrame_WithWrongId_ThrowsMismatch()
    {
        var dict = ZstdDictionary.FromRawPrefix("0123456789ABCDEF"u8, dictId: 8);
        var ex = Assert.Throws<ZstdException>(() =>
            ZstdDecompressor.Decompress(RawBlockDictFrame(7), dict));
        Assert.Contains("ID mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_PlainFrame_WithDict_StillDecodes()
    {
        // Frames without a dictionary ID decode with any supplied
        // dictionary active (stock ZSTD_decompress_usingDict behavior).
        var plain = new ZstdCompressor(ZstdCompressionOptions.FromLevel(6)).CompressBlock("hello world"u8);
        var dict = ZstdDictionary.FromRawPrefix("0123456789ABCDEF"u8, dictId: 7);
        Assert.Equal("hello world"u8.ToArray(), ZstdDecompressor.Decompress(plain, dict));
    }

    [Fact]
    public void FromBytes_AutoDetectsRaw()
    {
        var dict = ZstdDictionary.FromBytes("just some bytes"u8.ToArray());
        Assert.False(dict.IsFormatted);
        Assert.Equal(0, dict.DictId);
        Assert.Equal(15, dict.ContentSize);
    }

    [Fact]
    public void FromBytes_ShortMagicPrefix_IsRaw()
    {
        // Fewer than 8 bytes can never be formatted (stock rule).
        var dict = ZstdDictionary.FromBytes([0x37, 0xA4, 0x30]);
        Assert.False(dict.IsFormatted);
    }

    [Fact]
    public void FromRawPrefix_Empty_Throws()
    {
        Assert.Throws<ArgumentException>(() => ZstdDictionary.FromRawPrefix([]));
        Assert.Throws<ArgumentException>(() => ZstdDictionary.FromBytes([]));
    }

    [Fact]
    public void Decode_DictContentCountsTowardWindowCap()
    {
        // Frame window (1024) fits the cap; the 4096-byte dictionary does not.
        var dict = ZstdDictionary.FromRawPrefix(new byte[4096], dictId: 7);
        var options = new ZstdDecoderOptions { MaxWindowSize = 2048, MaxFrameContentSize = 1024 };
        var frame = RawBlockDictFrame(7);
        var ex = Assert.Throws<ZstdException>(() =>
            ZstdDecompressor.Decompress(frame, 0, frame.Length, dict, options));
        Assert.Contains("exceeds decoder limit", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Round-trips (encoder <-> decoder, raw prefix dictionaries)
    // ------------------------------------------------------------------

    private static readonly byte[] Phrase =
        "The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs. "u8.ToArray();

    internal static byte[] CycleText(int n)
    {
        var buf = new byte[n];
        for (var i = 0; i < n; i++)
        {
            buf[i] = Phrase[i % Phrase.Length];
        }

        return buf;
    }

    internal static byte[] PseudoRandom(int n, int seed)
    {
        var rng = new Random(seed);
        var buf = new byte[n];
        rng.NextBytes(buf);
        return buf;
    }

    internal static ZstdDictionary TextRawDict()
    {
        // 8 KiB phrase-text dictionary with a fixed 1-byte ID field width.
        return ZstdDictionary.FromRawPrefix(CycleText(8192), dictId: 7);
    }

    public static TheoryData<int, int, bool> DictRoundTripCases()
    {
        var data = new TheoryData<int, int, bool>();
        foreach (var size in new[] { 2048, 8192 })
        {
            foreach (var level in new[] { 1, 6, 12 })
            {
                data.Add(size, level, false);
                data.Add(size, level, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DictRoundTripCases))]
    public void RoundTrip_RawDict_Text(int size, int level, bool checksum)
    {
        var dict = TextRawDict();
        var input = CycleText(size);
        var options = new ZstdCompressionOptions { Level = level, ChecksumFlag = checksum, Dictionary = dict };

        var frame = new ZstdCompressor(options).CompressBlock(input);
        var decoded = ZstdDecompressor.Decompress(frame, dict);
        Assert.Equal(input, decoded);

        // The dictionary must actually help in its win zone.
        var plain = new ZstdCompressor(new ZstdCompressionOptions { Level = level, ChecksumFlag = checksum })
            .CompressBlock(input);
        Assert.True(frame.Length < plain.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void RoundTrip_RawDict_HeteroMultiBlock(int level)
    {
        // 200 KiB multi-block content with a 1-byte-ID raw dictionary.
        var dict = TextRawDict();
        var input = new byte[204800];
        Array.Copy(CycleText(102400), input, 102400);
        Array.Copy(PseudoRandom(102400, 0x5EED2026), 0, input, 102400, 102400);

        var frame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = level, Dictionary = dict }).CompressBlock(input);
        Assert.Equal(input, ZstdDecompressor.Decompress(frame, dict));
    }

    [Fact]
    public void RoundTrip_RawDict_EmptyInput()
    {
        var dict = TextRawDict();
        var frame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = 6, Dictionary = dict }).CompressBlock([]);
        Assert.Equal([], ZstdDecompressor.Decompress(frame, dict));
    }

    [Fact]
    public void RoundTrip_WrongContentDict_ThrowsMismatch()
    {
        var dict = TextRawDict();
        var other = ZstdDictionary.FromRawPrefix(PseudoRandom(8192, 42), dictId: 7);
        var input = CycleText(2048);

        // With a checksum, wrong-content history fails loudly.
        var checksummed = new ZstdCompressor(
            new ZstdCompressionOptions { Level = 6, ChecksumFlag = true, Dictionary = dict })
            .CompressBlock(input);
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(checksummed, other));

        // Without one, same-ID/different-content is undetectable garbage —
        // inherent to the format, identical in stock zstd (IDs, not content,
        // are verified). It must differ from the input, never "succeed" equal.
        var plain = new ZstdCompressor(
            new ZstdCompressionOptions { Level = 6, Dictionary = dict }).CompressBlock(input);
        Assert.NotEqual(input, ZstdDecompressor.Decompress(plain, other));
    }

    [Fact]
    public void RoundTrip_CompressBlockDictOverload_WinsOverOptions()
    {
        var options = new ZstdCompressor(new ZstdCompressionOptions { Level = 6 });
        var dict = TextRawDict();
        var input = CycleText(2048);
        var frame = options.CompressBlock(input, dict);
        Assert.Equal(input, ZstdDecompressor.Decompress(frame, dict));
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(frame));
    }

    [Fact]
    public void RoundTrip_TruncatedDictIdField_Throws()
    {
        // dictFlag=3 (4-byte ID) but the input ends after 1 ID byte.
        var truncated = new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x03, 0x00, 0x07 };
        var dict = TextRawDict();
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(truncated, dict));
    }

    // ------------------------------------------------------------------
    // Streams with dictionaries
    // ------------------------------------------------------------------

    private static byte[] CompressViaDictStream(byte[] input, ZstdDictionary dict, int chunk)
    {
        using var dest = new MemoryStream();
        var options = new ZstdCompressionOptions { Level = 6, ChecksumFlag = true, Dictionary = dict };
        using (var enc = new ZstdCompressionStream(dest, options, leaveOpen: true))
        {
            for (var i = 0; i < input.Length; i += chunk)
            {
                enc.Write(input, i, Math.Min(chunk, input.Length - i));
            }
        }

        return dest.ToArray();
    }

    private static byte[] DecompressViaDictStream(byte[] frame, ZstdDictionary? dict)
    {
        using var src = new MemoryStream(frame);
        using var dec = new ZstdDecompressionStream(src, ZstdDecoderOptions.Default, dict);
        using var result = new MemoryStream();
        dec.CopyTo(result);
        return result.ToArray();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4096)]
    public void StreamDict_RoundTrip(int chunk)
    {
        var dict = TextRawDict();
        var input = CycleText(8192);
        var frame = CompressViaDictStream(input, dict, chunk);
        Assert.Equal(input, DecompressViaDictStream(frame, dict));
    }

    [Fact]
    public void StreamDict_SingleShotInterchange()
    {
        var dict = TextRawDict();
        var input = CycleText(2048);

        // Stream output decodes single-shot and vice versa.
        var streamFrame = CompressViaDictStream(input, dict, 512);
        Assert.Equal(input, ZstdDecompressor.Decompress(streamFrame, dict));

        var singleFrame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = 6, ChecksumFlag = true, Dictionary = dict })
            .CompressBlock(input);
        Assert.Equal(input, DecompressViaDictStream(singleFrame, dict));
    }

    [Fact]
    public void StreamDict_RequiresDict()
    {
        var dict = TextRawDict();
        var frame = CompressViaDictStream(CycleText(1024), dict, 1024);

        using var src = new MemoryStream(frame);
        using var dec = new ZstdDecompressionStream(src);
        Assert.Throws<ZstdException>(() => dec.CopyTo(Stream.Null));
    }

    [Fact]
    public void StreamDict_WrongId_ThrowsMismatch()
    {
        var dict = TextRawDict();
        var other = ZstdDictionary.FromRawPrefix(CycleText(8192), dictId: 9);
        var frame = CompressViaDictStream(CycleText(1024), dict, 1024);

        using var src = new MemoryStream(frame);
        using var dec = new ZstdDecompressionStream(src, ZstdDecoderOptions.Default, other);
        var ex = Assert.Throws<ZstdException>(() => dec.CopyTo(Stream.Null));
        Assert.Contains("ID mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamDict_RoundTripAsync()
    {
        var dict = TextRawDict();
        var input = CycleText(4096);
        using var dest = new MemoryStream();
        var options = new ZstdCompressionOptions { Level = 6, Dictionary = dict };
        await using (var enc = new ZstdCompressionStream(dest, options, leaveOpen: true))
        {
            await enc.WriteAsync(input);
        }

        Assert.Equal(input, DecompressViaDictStream(dest.ToArray(), dict));
    }

    // ------------------------------------------------------------------
    // Stock interop: committed libzstd-1.5.7 goldens (Goldens/zstd/dict-*)
    // ------------------------------------------------------------------

    private static string GoldensDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "CSharp_ZArchiveSharp.sln")))
            {
                return Path.Combine(dir, "ZArchiveSharp.Tests", "Goldens", "zstd");
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Repo root not found.");
    }

    internal static ZstdDictionary StockWordsDict()
    {
        var bytes = File.ReadAllBytes(Path.Combine(GoldensDir(), "dict-words.dict"));
        var dict = ZstdDictionary.FromBytes(bytes);
        Assert.True(dict.IsFormatted);
        return dict;
    }

    public static TheoryData<string, string> StockGoldenCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in new[] { "small", "medium" })
        {
            foreach (var level in new[] { "l1", "l6", "l12" })
            {
                data.Add(name, level);
            }
        }

        data.Add("big", "l6");
        data.Add("big", "l12");
        return data;
    }

    [Theory]
    [MemberData(nameof(StockGoldenCases))]
    public void StockDictFrames_Decode(string name, string level)
    {
        // Real libzstd-1.5.7 dict frames (compression.zstd, trained 8 KiB
        // word dict): our decoder must reproduce the plaintext exactly with
        // no toolchain present.
        var dir = GoldensDir();
        var dict = StockWordsDict();
        var frame = File.ReadAllBytes(Path.Combine(dir, $"dict-stock-{name}-{level}.zst"));
        var expected = File.ReadAllBytes(Path.Combine(dir, $"dict-stock-{name}.bin"));
        Assert.Equal(expected, ZstdDecompressor.Decompress(frame, dict));
    }

    [Theory]
    [MemberData(nameof(StockGoldenCases))]
    public void StockDictFrames_DecodeViaStream(string name, string level)
    {
        var dir = GoldensDir();
        var dict = StockWordsDict();
        var frame = File.ReadAllBytes(Path.Combine(dir, $"dict-stock-{name}-{level}.zst"));
        var expected = File.ReadAllBytes(Path.Combine(dir, $"dict-stock-{name}.bin"));
        Assert.Equal(expected, DecompressViaDictStream(frame, dict));
    }

    [Fact]
    public void StockDict_HasExpectedId()
    {
        Assert.Equal(0x02F4E1B5, StockWordsDict().DictId);
    }

    // ------------------------------------------------------------------
    // Synthetic formatted dictionaries (no stock needed)
    // ------------------------------------------------------------------

    // Minimal valid formatted dictionary, assembled by hand: magic, ID
    // 0x12345678, a 2-weight Huffman table, single-symbol FSE tables, repeat
    // offsets {5, 4, 8}, 16 content bytes.
    internal static byte[] SyntheticFormattedDict()
    {
        return
        [
            0x37, 0xA4, 0x30, 0xEC, // magic
            0x78, 0x56, 0x34, 0x12, // dictID
            0x81, 0x11, // Huffman: 2 direct weights {1,1}
            0xF0, 0x03, // OF: single-symbol table
            0xF0, 0x03, // ML: single-symbol table
            0xF0, 0x03, // LL: single-symbol table
            0x05, 0x00, 0x00, 0x00, // rep0 = 5
            0x04, 0x00, 0x00, 0x00, // rep1 = 4
            0x08, 0x00, 0x00, 0x00, // rep2 = 8
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
            0x38, 0x39, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, // "0123456789ABCDEF"
        ];
    }

    [Fact]
    public void SyntheticDict_Parses()
    {
        var dict = ZstdDictionary.FromBytes(SyntheticFormattedDict());
        Assert.True(dict.IsFormatted);
        Assert.Equal(0x12345678, dict.DictId);
        Assert.Equal(16, dict.ContentSize);
        Assert.Equal("0123456789ABCDEF"u8.ToArray(), DecompressDictContent(dict));
    }

    private static byte[] DecompressDictContent(ZstdDictionary dict)
    {
        // Read back through a 4-byte-ID frame carrying only a raw block, so
        // the test observes exactly the seeded history behavior.
        var frame = new byte[]
        {
            0x28, 0xB5, 0x2F, 0xFD, 0x03, 0x00,
            0x78, 0x56, 0x34, 0x12,
            0x81, 0x00, 0x00, // raw last block, 16 bytes
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
            0x38, 0x39, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46,
        };
        return ZstdDecompressor.Decompress(frame, dict);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void SyntheticDict_RoundTrip_UsesDictReps(int level)
    {
        // "ABCDE" runs match at distance 5: the encoder emits repcodes from
        // the dictionary's {5, 4, 8} history, so decode proves rep seeding.
        var dict = ZstdDictionary.FromBytes(SyntheticFormattedDict());
        var input = new byte[500];
        for (var i = 0; i < input.Length; i++)
        {
            input[i] = (byte)("ABCDE"[i % 5]);
        }

        var frame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = level, ChecksumFlag = true, Dictionary = dict })
            .CompressBlock(input);
        Assert.Equal(input, ZstdDecompressor.Decompress(frame, dict));
    }

    [Fact]
    public void SyntheticDict_Truncated_Throws()
    {
        var full = SyntheticFormattedDict();
        foreach (var len in new[] { 8, 10, 12, 20, full.Length - 13 })
        {
            var ex = Assert.Throws<ZstdException>(() => ZstdDictionary.FromBytes(full[..len]));
            Assert.Contains("Invalid zstd dictionary", ex.Message, StringComparison.Ordinal);
        }

        // Truncating only content stays valid (stock: content is the rest).
        var shorter = ZstdDictionary.FromBytes(full[..(full.Length - 1)]);
        Assert.True(shorter.IsFormatted);
        Assert.Equal(15, shorter.ContentSize);
    }

    [Fact]
    public void SyntheticDict_BadRep_Throws()
    {
        var full = SyntheticFormattedDict();
        var badZero = SyntheticFormattedDict();
        badZero[16] = 0x00; // rep0 = 0
        Assert.Contains("Invalid zstd dictionary",
            Assert.Throws<ZstdException>(() => ZstdDictionary.FromBytes(badZero)).Message, StringComparison.Ordinal);

        var badBig = SyntheticFormattedDict();
        badBig[16] = 0x11; // rep0 = 17 > 16 content bytes
        Assert.Contains("Invalid zstd dictionary",
            Assert.Throws<ZstdException>(() => ZstdDictionary.FromBytes(badBig)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SyntheticDict_EmptyContent_Throws()
    {
        var noContent = SyntheticFormattedDict()[..28]; // ends right after reps
        Assert.Contains("Invalid zstd dictionary",
            Assert.Throws<ZstdException>(() => ZstdDictionary.FromBytes(noContent)).Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Stock decodes ours (toolchain: python compression.zstd, skipped when
    // absent). Our dict frames must decode in real libzstd 1.5.7.
    // ------------------------------------------------------------------

    private static string? FindPythonWithZstd()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? ["python", "python3"]
            : ["python3", "python"];
        foreach (var candidate in candidates)
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    ArgumentList = { "-c", "import compression.zstd" },
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (probe is null)
                {
                    continue;
                }

                probe.WaitForExit(15000);
                if (probe.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // Candidate missing - try the next.
            }
        }

        return null;
    }

    private const string StockDecodeScript = """
        import sys, compression.zstd
        frame = open(sys.argv[1], 'rb').read()
        raw = open(sys.argv[2], 'rb').read()
        if sys.argv[3] == 'raw':
            zd = compression.zstd.ZstdDict(raw, is_raw=True).as_prefix
        else:
            zd = compression.zstd.ZstdDict(raw)
        sys.stdout.buffer.write(compression.zstd.decompress(frame, zstd_dict=zd))
        """;

    private static byte[] DecodeWithStockPython(string python, byte[] frame, byte[] dictBytes, string mode)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"zarsharp-dict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var framePath = Path.Combine(dir, "frame.zst");
            var dictPath = Path.Combine(dir, "dict.bin");
            File.WriteAllBytes(framePath, frame);
            File.WriteAllBytes(dictPath, dictBytes);
            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(StockDecodeScript);
            psi.ArgumentList.Add(framePath);
            psi.ArgumentList.Add(dictPath);
            psi.ArgumentList.Add(mode);
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start python.");
            using var stdout = new MemoryStream();
            proc.StandardOutput.BaseStream.CopyTo(stdout);
            if (!proc.WaitForExit(60000))
            {
                proc.Kill();
                throw new TimeoutException("Stock python decode timed out.");
            }

            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"Stock python decode failed (exit {proc.ExitCode}).");
            }

            return stdout.ToArray();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StockDecodes_Ours_RawDict_SingleShot()
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        // ID-0 raw dictionaries write no ID field: stock loads the bytes as
        // a prefix, the prefix-mode interop path.
        var dictContent = CycleText(1024);
        var dict = ZstdDictionary.FromRawPrefix(dictContent);
        var input = CycleText(2048);
        var frame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = 6, ChecksumFlag = true, Dictionary = dict })
            .CompressBlock(input);
        Assert.Equal(input, DecodeWithStockPython(python, frame, dictContent, "raw"));
    }

    [Fact]
    public void StockDecodes_Ours_RawDict_Stream()
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        var dictContent = CycleText(1024);
        var dict = ZstdDictionary.FromRawPrefix(dictContent);
        var input = CycleText(2048);
        Assert.Equal(input, DecodeWithStockPython(python, CompressViaDictStream(input, dict, 512), dictContent, "raw"));
    }

    [Theory]
    [InlineData("medium", 6)]
    [InlineData("medium", 12)]
    [InlineData("big", 12)]
    public void StockDecodes_Ours_TrainedDict(string name, int level)
    {
        var python = FindPythonWithZstd();
        if (python is null)
        {
            return;
        }

        // In-domain plaintexts for the trained word dict, including a
        // multi-block case (big).
        var dir = GoldensDir();
        var dictBytes = File.ReadAllBytes(Path.Combine(dir, "dict-words.dict"));
        var dict = ZstdDictionary.FromBytes(dictBytes);
        var input = File.ReadAllBytes(Path.Combine(dir, $"dict-stock-{name}.bin"));
        var frame = new ZstdCompressor(
            new ZstdCompressionOptions { Level = level, ChecksumFlag = true, Dictionary = dict })
            .CompressBlock(input);
        Assert.Equal(input, DecodeWithStockPython(python, frame, dictBytes, "formatted"));
    }

    // ------------------------------------------------------------------
    // Fixed-seed fuzz (plan 3.5: 0x5EED2026 family)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void Fuzz_DictRoundTrip_FixedSeed(int level)
    {
        // Deterministic streams from the master seed: random dictionaries +
        // inputs mixing dictionary slices (forced history matches) and fresh
        // randomness, round-tripped single-shot and via streams.
        for (var iter = 0; iter < 25; iter++)
        {
            var rng = new Random(unchecked((int)(0x5EED2026u + (uint)iter * 0x9E3779B9u + (uint)level * 131u)));
            var dictBytes = new byte[rng.Next(64, 1500)];
            rng.NextBytes(dictBytes);
            var dict = ZstdDictionary.FromRawPrefix(dictBytes, dictId: (uint)rng.Next(1, 70000));

            var input = new byte[rng.Next(0, 3000)];
            for (var i = 0; i < input.Length;)
            {
                if (rng.Next(2) == 0 && dictBytes.Length > 8)
                {
                    var take = Math.Min(rng.Next(4, 64), Math.Min(dictBytes.Length, input.Length - i));
                    var from = rng.Next(0, dictBytes.Length - take + 1);
                    Array.Copy(dictBytes, from, input, i, take);
                    i += take;
                }
                else
                {
                    input[i++] = (byte)rng.Next(256);
                }
            }

            var checksum = iter % 2 == 0;
            var options = new ZstdCompressionOptions { Level = level, ChecksumFlag = checksum, Dictionary = dict };
            var frame = new ZstdCompressor(options).CompressBlock(input);
            Assert.Equal(input, ZstdDecompressor.Decompress(frame, dict));
            Assert.Equal(input, DecompressViaDictStream(frame, dict));
        }
    }
}
