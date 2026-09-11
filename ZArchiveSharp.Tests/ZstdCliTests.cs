using ZArchiveSharp.Pipeline;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for <see cref="ZstdCli"/> (<c>zar zstd</c>: arg parsing, stream
/// pipes, file round-trips, exit codes) and for archive pack/extract with
/// <see cref="ZarPipelineOptions.Dictionary"/>.
/// </summary>
public sealed class ZstdCliTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _log = [];
    private readonly List<string> _errors = [];

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

    private static byte[] CycleBytes(int size)
    {
        const string alphabet = "the quick brown fox jumps over the lazy dog 0123456789\npack my box with five dozen liquor jugs! ";
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)alphabet[(i * 31 + i / 7) % alphabet.Length];
        }

        return data;
    }

    private static byte[] HeteroBytes(int size, int seed)
    {
        // Text-like runs interleaved with seeded random chunks (multi-block,
        // only partly compressible) — deterministic, no HashCode.Combine.
        var rng = new Random(seed);
        var data = CycleBytes(size);
        var chunk = new byte[257];
        for (int i = 0; i < size; i += 4096)
        {
            rng.NextBytes(chunk);
            int n = Math.Min(chunk.Length, size - i);
            Array.Copy(chunk, 0, data, i, n);
        }

        return data;
    }

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

    private static byte[] PatchedIdDict(byte[] formatted, uint newId)
    {
        // A formatted dict with a different ID but valid tables/content:
        // frame decoding must reject it as a mismatch before touching bytes.
        var copy = formatted.ToArray();
        copy[4] = (byte)newId;
        copy[5] = (byte)(newId >> 8);
        copy[6] = (byte)(newId >> 16);
        copy[7] = (byte)(newId >> 24);
        return copy;
    }

    private static ZstdCli.ZstdJob CompressJob(
        string? input = null, string? output = null, int level = 6,
        string? dict = null, bool checksum = false, bool quiet = false) => new()
        {
            Compress = true,
            InputPath = input,
            OutputPath = output,
            Level = level,
            DictPath = dict,
            Checksum = checksum,
            Quiet = quiet,
        };

    private static ZstdCli.ZstdJob DecompressJob(
        string? input = null, string? output = null, string? dict = null, bool quiet = false) => new()
        {
            Compress = false,
            InputPath = input,
            OutputPath = output,
            DictPath = dict,
            Quiet = quiet,
        };

    private Task<int> RunStreamsAsync(ZstdCli.ZstdJob job, MemoryStream stdin, MemoryStream stdout, CancellationToken ct = default)
    {
        _log.Clear();
        _errors.Clear();
        return ZstdCli.RunAsync(job, stdin, stdout, _log.Add, _errors.Add, ct);
    }

    // ------------------------------------------------------------------
    // Arg parsing
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_ModeRequired()
    {
        Assert.False(ZstdCli.TryParse([], out _, out string? error));
        Assert.Contains("-c", error, StringComparison.Ordinal);
        Assert.False(ZstdCli.TryParse(["in", "out"], out _, out error));
        Assert.Contains("-d", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_BothModes_Rejects()
    {
        Assert.False(ZstdCli.TryParse(["-c", "-d"], out _, out string? error));
        Assert.Contains("Cannot combine", error, StringComparison.Ordinal);
        Assert.False(ZstdCli.TryParse(["--decompress", "--compress"], out _, out _));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("-z")]
    public void Parse_UnknownOption_Rejects(string flag)
    {
        Assert.False(ZstdCli.TryParse(["-c", flag], out _, out string? error));
        Assert.Contains(flag, error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("23")]
    [InlineData("abc")]
    [InlineData("")]
    public void Parse_BadLevel_Rejects(string level)
    {
        Assert.False(ZstdCli.TryParse(["-c", "-l", level], out _, out string? error));
        Assert.Contains("level", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_MissingValues_Reject()
    {
        Assert.False(ZstdCli.TryParse(["-c", "-l"], out _, out _));
        Assert.False(ZstdCli.TryParse(["-c", "--dict"], out _, out _));
        Assert.False(ZstdCli.TryParse(["-c", "a", "b", "c"], out _, out string? tooMany));
        Assert.Contains("Too many", tooMany, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Defaults()
    {
        Assert.True(ZstdCli.TryParse(["-c"], out var job, out _));
        Assert.NotNull(job);
        Assert.True(job.Compress);
        Assert.Null(job.InputPath);
        Assert.Null(job.OutputPath);
        Assert.Equal(6, job.Level);
        Assert.Null(job.DictPath);
        Assert.False(job.Checksum);
        Assert.False(job.Quiet);
        Assert.False(job.ShowHelp);
    }

    [Fact]
    public void Parse_FullJob()
    {
        Assert.True(ZstdCli.TryParse(
            ["--decompress", "--dict", "d.dict", "--check", "-q", "in.zst", "out.bin"],
            out var job, out _));
        Assert.NotNull(job);
        Assert.False(job.Compress);
        Assert.Equal("d.dict", job.DictPath);
        Assert.True(job.Checksum);
        Assert.True(job.Quiet);
        Assert.Equal("in.zst", job.InputPath);
        Assert.Equal("out.bin", job.OutputPath);
    }

    [Fact]
    public void Parse_CheckLastWins()
    {
        Assert.True(ZstdCli.TryParse(["-c", "--check", "--no-check"], out var a, out _));
        Assert.False(a!.Checksum);
        Assert.True(ZstdCli.TryParse(["-c", "--no-check", "--check"], out var b, out _));
        Assert.True(b!.Checksum);
    }

    [Fact]
    public void Parse_StdoutWithOutputPath_Rejects()
    {
        Assert.False(ZstdCli.TryParse(["-c", "--stdout", "in", "out"], out _, out string? error));
        Assert.Contains("--stdout", error, StringComparison.Ordinal);
        Assert.True(ZstdCli.TryParse(["-c", "--stdout", "in"], out var job, out _));
        Assert.Null(job!.OutputPath);
    }

    [Fact]
    public void Parse_InheritedDefaults_Overridable()
    {
        Assert.True(ZstdCli.TryParse(["-c"], out var job, out _,
            defaultLevel: 3, defaultDictPath: "g.dict", defaultChecksum: true, defaultQuiet: true, defaultStdout: true));
        Assert.NotNull(job);
        Assert.Equal(3, job.Level);
        Assert.Equal("g.dict", job.DictPath);
        Assert.True(job.Checksum);
        Assert.True(job.Quiet);

        // Explicit flags win over inherited ones.
        Assert.True(ZstdCli.TryParse(["-c", "-l", "9", "--no-check", "--dict", "x"], out var over, out _,
            defaultLevel: 3, defaultDictPath: "g.dict", defaultChecksum: true, defaultQuiet: false));
        Assert.Equal(9, over!.Level);
        Assert.Equal("x", over.DictPath);
        Assert.False(over.Checksum);

        // Inherited stdout conflicts with an explicit output path, like explicit --stdout.
        Assert.False(ZstdCli.TryParse(["-c", "in", "out"], out _, out _, defaultStdout: true));

        // Inherited bad level is a parse failure, not a crash.
        Assert.False(ZstdCli.TryParse(["-c"], out _, out _, defaultLevel: 99));
    }

    [Fact]
    public void Parse_Help_ShortCircuits()
    {
        Assert.True(ZstdCli.TryParse(["--help"], out var job, out _));
        Assert.True(job!.ShowHelp);
        Assert.True(ZstdCli.TryParse(["-c", "--help"], out var mixed, out _));
        Assert.True(mixed!.ShowHelp);
    }

    // ------------------------------------------------------------------
    // Stream pipes (stdin/stdout analogues)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Streams_RoundTrip_MultiBlock()
    {
        var data = HeteroBytes(200_000, seed: 0x5EED2026);
        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(CompressJob(), new MemoryStream(data, writable: false), compressed));
        Assert.Contains(_log, l => l.Contains("Compressing stdin -> stdout", StringComparison.Ordinal));
        Assert.Empty(_errors);
        Assert.True(compressed.Length < data.Length);

        // Stream bytes decode with the single-shot decoder (framing interop).
        Assert.Equal(data, ZstdDecompressor.Decompress(compressed.ToArray()));

        compressed.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(DecompressJob(), compressed, back));
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task Streams_RoundTrip_10MiB_Pipe()
    {
        // Phase 3 acceptance: 10 MiB through pipes at L6.
        var data = HeteroBytes(10 * 1024 * 1024, seed: 0xC10C);
        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(CompressJob(level: 6), new MemoryStream(data, writable: false), compressed));
        compressed.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(DecompressJob(), compressed, back));
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task Streams_Checksum_RoundTrip()
    {
        var data = CycleBytes(50_000);
        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(CompressJob(checksum: true), new MemoryStream(data, writable: false), compressed));
        Assert.Contains(_log, l => l.Contains("+checksum", StringComparison.Ordinal));
        compressed.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(DecompressJob(), compressed, back));
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task Streams_Quiet_SuppressesLog()
    {
        var data = CycleBytes(1000);
        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(CompressJob(quiet: true), new MemoryStream(data, writable: false), compressed));
        Assert.Empty(_log);
    }

    [Fact]
    public async Task Streams_Help_PrintsUsage()
    {
        using var stdin = new MemoryStream();
        using var stdout = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(new ZstdCli.ZstdJob { ShowHelp = true }, stdin, stdout));
        Assert.Contains(_log, l => l.Contains("zar zstd -c|-d", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Streams_BadJobLevel_Rejects()
    {
        using var stdin = new MemoryStream();
        using var stdout = new MemoryStream();
        Assert.Equal(ZarchiveCli.BadUsage,
            await RunStreamsAsync(new ZstdCli.ZstdJob { Compress = true, Level = 99 }, stdin, stdout));
        Assert.Contains(_errors, l => l.Contains("level", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Streams_Cancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var data = CycleBytes(100_000);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunStreamsAsync(CompressJob(), new MemoryStream(data, writable: false), new MemoryStream(), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunStreamsAsync(DecompressJob(), new MemoryStream(data, writable: false), new MemoryStream(), cts.Token));
    }

    [Fact]
    public async Task Streams_CorruptInput_MapsExtractCode()
    {
        using var stdin = new MemoryStream([0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4], writable: false);
        using var stdout = new MemoryStream();
        Assert.Equal(ZarchiveCli.ExtractionFailed, await RunStreamsAsync(DecompressJob(), stdin, stdout));
        Assert.Contains(_errors, l => l.Contains("decompression failed", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // Stream dict behavior
    // ------------------------------------------------------------------

    [Fact]
    public async Task Streams_Dict_RoundTrip_RawPrefix()
    {
        var dictBytes = CycleBytes(8192);
        var dict = ZstdDictionary.FromRawPrefix(dictBytes, dictId: 7);
        var data = new byte[2048];
        Array.Copy(dictBytes, 100, data, 0, 1500);
        new Random(42).NextBytes(data.AsSpan(1500));

        var dictFile = Path.Combine(NewTempDir("zstdcli_dict"), "raw.dict");
        await File.WriteAllBytesAsync(dictFile, dictBytes);

        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok, await RunStreamsAsync(
            CompressJob(dict: dictFile), new MemoryStream(data, writable: false), compressed));
        Assert.Contains(_log, l => l.Contains("dict", StringComparison.OrdinalIgnoreCase));

        // Plain decode of a dict frame must fail, never return garbage.
        Assert.Throws<ZstdException>(() => ZstdDecompressor.Decompress(compressed.ToArray()));

        compressed.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok, await RunStreamsAsync(
            DecompressJob(dict: dictFile), compressed, back));
        Assert.Equal(data, back.ToArray());
        Assert.True(dict.ContentSize > 0);
    }

    [Fact]
    public async Task Streams_DictFrame_WithoutDict_Fails()
    {
        // A formatted dict writes its ID into frames; decoding without it
        // must fail with "requires a dictionary", never garbage.
        var dir = NewTempDir("zstdcli_req");
        var dictFile = Path.Combine(dir, "words.dict");
        await File.WriteAllBytesAsync(dictFile,
            await File.ReadAllBytesAsync(Path.Combine(GoldensDir(), "dict-words.dict")));
        var data = CycleBytes(2048);

        using var compressed = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok, await RunStreamsAsync(
            CompressJob(dict: dictFile), new MemoryStream(data, writable: false), compressed));

        compressed.Position = 0;
        Assert.Equal(ZarchiveCli.ExtractionFailed,
            await RunStreamsAsync(DecompressJob(), compressed, new MemoryStream()));
        Assert.Contains(_errors, l => l.Contains("requires a dictionary", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Streams_PlainFrame_WithDict_SucceedsIdentical()
    {
        var data = CycleBytes(20_000);
        using var plain = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(CompressJob(), new MemoryStream(data, writable: false), plain));

        // A supplied dictionary is inert for plain frames (Phase 2 semantics).
        var dictFile = Path.Combine(NewTempDir("zstdcli_inert"), "d.dict");
        await File.WriteAllBytesAsync(dictFile, CycleBytes(1024));
        plain.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok,
            await RunStreamsAsync(DecompressJob(dict: dictFile), plain, back));
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task Streams_MissingDictFile_IsBadUsage()
    {
        var missing = Path.Combine(NewTempDir("zstdcli_miss"), "nope.dict");
        using var stdin = new MemoryStream(CycleBytes(100), writable: false);
        Assert.Equal(ZarchiveCli.BadUsage,
            await RunStreamsAsync(CompressJob(dict: missing), stdin, new MemoryStream()));
        Assert.Contains(_errors, l => l.Contains("dictionary file not found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Streams_CorruptDictFile_IsBadUsage()
    {
        var dir = NewTempDir("zstdcli_baddict");
        var bad = Path.Combine(dir, "bad.dict");
        // Formatted magic + truncated body: structurally invalid.
        await File.WriteAllBytesAsync(bad, [0x37, 0xA4, 0x30, 0xEC, 0x01, 0x02, 0x03, 0x04, 0xFF]);
        using var stdin = new MemoryStream(CycleBytes(100), writable: false);
        Assert.Equal(ZarchiveCli.BadUsage,
            await RunStreamsAsync(CompressJob(dict: bad), stdin, new MemoryStream()));
        Assert.Contains(_errors, l => l.Contains("invalid dictionary", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------
    // File paths
    // ------------------------------------------------------------------

    [Fact]
    public async Task Files_RoundTrip_FileToFile()
    {
        var dir = NewTempDir("zstdcli_files");
        var data = HeteroBytes(300_000, seed: 77);
        var src = Path.Combine(dir, "in.bin");
        var zst = Path.Combine(dir, "in.bin.zst");
        var back = Path.Combine(dir, "back.bin");
        await File.WriteAllBytesAsync(src, data);

        _log.Clear();
        _errors.Clear();
        Assert.Equal(ZarchiveCli.Ok,
            await ZstdCli.RunAsync(CompressJob(input: src, output: zst), Stream.Null, Stream.Null, _log.Add, _errors.Add));
        Assert.Contains(_log, l => l.Contains(src, StringComparison.Ordinal) && l.Contains(zst, StringComparison.Ordinal));

        _log.Clear();
        Assert.Equal(ZarchiveCli.Ok,
            await ZstdCli.RunAsync(DecompressJob(input: zst, output: back), Stream.Null, Stream.Null, _log.Add, _errors.Add));
        Assert.Equal(data, await File.ReadAllBytesAsync(back));
    }

    [Fact]
    public async Task Files_StdoutBytes_EqualFileBytes()
    {
        var dir = NewTempDir("zstdcli_stdout");
        var data = CycleBytes(64_000);
        var src = Path.Combine(dir, "in.bin");
        var zst = Path.Combine(dir, "out.zst");
        await File.WriteAllBytesAsync(src, data);

        using var viaStdout = new MemoryStream();
        Assert.Equal(ZarchiveCli.Ok, await RunStreamsAsync(
            CompressJob(input: src), new MemoryStream(), viaStdout));

        _errors.Clear();
        Assert.Equal(ZarchiveCli.Ok,
            await ZstdCli.RunAsync(CompressJob(input: src, output: zst), Stream.Null, Stream.Null, null, _errors.Add));
        Assert.Equal(await File.ReadAllBytesAsync(zst), viaStdout.ToArray());
    }

    [Fact]
    public async Task Files_OutputExists_RefusesWithoutTouching()
    {
        var dir = NewTempDir("zstdcli_refuse");
        var src = Path.Combine(dir, "in.bin");
        var zst = Path.Combine(dir, "out.zst");
        await File.WriteAllBytesAsync(src, CycleBytes(1000));
        await File.WriteAllBytesAsync(zst, [9, 9, 9]);

        _errors.Clear();
        Assert.Equal(ZarchiveCli.Refused,
            await ZstdCli.RunAsync(CompressJob(input: src, output: zst), Stream.Null, Stream.Null, null, _errors.Add));
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(zst));

        Assert.Equal(ZarchiveCli.Refused,
            await ZstdCli.RunAsync(DecompressJob(input: src, output: zst), Stream.Null, Stream.Null, null, _errors.Add));
    }

    [Fact]
    public async Task Files_MissingInput_MapsCodes()
    {
        var dir = NewTempDir("zstdcli_noinput");
        var missing = Path.Combine(dir, "missing.bin");
        var out1 = Path.Combine(dir, "o1");
        _errors.Clear();
        Assert.Equal(ZarchiveCli.InputNotReadable,
            await ZstdCli.RunAsync(CompressJob(input: missing, output: out1), Stream.Null, Stream.Null, null, _errors.Add));
        Assert.Equal(ZarchiveCli.NotFound,
            await ZstdCli.RunAsync(DecompressJob(input: missing, output: out1), Stream.Null, Stream.Null, null, _errors.Add));
        Assert.False(File.Exists(out1));
    }

    [Fact]
    public async Task Files_WrongDict_DeletesPartialOutput()
    {
        // Formatted dicts carry IDs: a different (but structurally valid)
        // dictionary must be rejected as a mismatch, deleting the partial.
        var dir = NewTempDir("zstdcli_wrongdict");
        var golden = await File.ReadAllBytesAsync(Path.Combine(GoldensDir(), "dict-words.dict"));
        var dictA = Path.Combine(dir, "a.dict");
        var dictB = Path.Combine(dir, "b.dict");
        await File.WriteAllBytesAsync(dictA, golden);
        await File.WriteAllBytesAsync(dictB, PatchedIdDict(golden, 0xDEADBEEF));

        var data = CycleBytes(2048);
        var src = Path.Combine(dir, "in.bin");
        var zst = Path.Combine(dir, "in.zst");
        var back = Path.Combine(dir, "back.bin");
        await File.WriteAllBytesAsync(src, data);

        _errors.Clear();
        Assert.Equal(ZarchiveCli.Ok,
            await ZstdCli.RunAsync(CompressJob(input: src, output: zst, dict: dictA), Stream.Null, Stream.Null, null, _errors.Add));

        Assert.Equal(ZarchiveCli.ExtractionFailed,
            await ZstdCli.RunAsync(DecompressJob(input: zst, output: back, dict: dictB), Stream.Null, Stream.Null, null, _errors.Add));
        Assert.Contains(_errors, l => l.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(back));
    }

    // ------------------------------------------------------------------
    // Archive pack/extract with a dictionary
    // ------------------------------------------------------------------

    private static (string Src, byte[] Expected) DictArchiveFixture(string root)
    {
        // Small files sharing one boilerplate: the dictionary-win zone.
        // Each file is one 64 KiB block: 2x boilerplate + unique tail.
        var boilerplate = CycleBytes(8192);
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var expected = new Dictionary<string, byte[]>();
        for (int f = 0; f < 8; f++)
        {
            var content = new byte[2 * boilerplate.Length + 256];
            Array.Copy(boilerplate, 0, content, 0, boilerplate.Length);
            Array.Copy(boilerplate, 0, content, boilerplate.Length, boilerplate.Length);
            var tail = HeteroBytes(256, seed: 1000 + f);
            Array.Copy(tail, 0, content, 2 * boilerplate.Length, 256);
            string name = $"file{f}.bin";
            File.WriteAllBytes(Path.Combine(src, name), content);
            expected[name] = content;
        }

        return (src, expected.Values.SelectMany(b => b).ToArray());
    }

    [Fact]
    public void Archive_Dict_PackExtract_RoundTrip()
    {
        var root = NewTempDir("zstdcli_arch");
        var (src, _) = DictArchiveFixture(root);
        var dictBytes = CycleBytes(8192);
        var dict = ZstdDictionary.FromRawPrefix(dictBytes, dictId: 0xAABBCCDD);

        var zar = Path.Combine(root, "dict.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions { Level = 6, Dictionary = dict });

        // The dictionary must actually engage: at least one block becomes a
        // dict frame, so the archive differs from a plain pack.
        var plainZar = Path.Combine(root, "plain.zar");
        ZarPipeline.Pack(src, plainZar, new ZarPipelineOptions { Level = 6 });
        Assert.NotEqual(File.ReadAllBytes(plainZar), File.ReadAllBytes(zar));

        var dest = Path.Combine(root, "out");
        ZarPipeline.Extract(zar, dest, new ZarPipelineOptions { Dictionary = dict });
        foreach (var file in Directory.GetFiles(src))
        {
            Assert.Equal(
                File.ReadAllBytes(file),
                File.ReadAllBytes(Path.Combine(dest, Path.GetFileName(file))));
        }
    }

    [Fact]
    public void Archive_Dict_ExtractWithoutDict_Fails()
    {
        var root = NewTempDir("zstdcli_archfail");
        var (src, _) = DictArchiveFixture(root);
        var dict = ZstdDictionary.FromRawPrefix(CycleBytes(8192), dictId: 0xAABBCCDD);
        var zar = Path.Combine(root, "dict.zar");
        ZarPipeline.Pack(src, zar, new ZarPipelineOptions { Level = 6, Dictionary = dict });

        // Same code path the CLI reports: extraction fails, never garbage.
        Assert.Equal(ZarchiveCli.ExtractionFailed,
            ZarchiveCli.Run([zar, Path.Combine(root, "out")], new ZarPipelineOptions(), log: _log.Add));
    }

    [Fact]
    public void Archive_Plain_ExtractWithDict_Identical()
    {
        var root = NewTempDir("zstdcli_archinert");
        var (src, _) = DictArchiveFixture(root);
        var plainZar = Path.Combine(root, "plain.zar");
        ZarPipeline.Pack(src, plainZar, new ZarPipelineOptions { Level = 6 });

        // A supplied dictionary is inert for plain archives.
        var destPlain = Path.Combine(root, "out_plain");
        var destDict = Path.Combine(root, "out_dict");
        ZarPipeline.Extract(plainZar, destPlain);
        ZarPipeline.Extract(plainZar, destDict,
            new ZarPipelineOptions { Dictionary = ZstdDictionary.FromRawPrefix(CycleBytes(8192)) });
        foreach (var file in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(file);
            Assert.Equal(File.ReadAllBytes(Path.Combine(destPlain, name)), File.ReadAllBytes(Path.Combine(destDict, name)));
        }
    }

    [Fact]
    public void Archive_Dict_LevelHonored()
    {
        // Acceptance: pack honors --level and --dict together.
        var root = NewTempDir("zstdcli_archlvl");
        var (src, _) = DictArchiveFixture(root);
        var dict = ZstdDictionary.FromRawPrefix(CycleBytes(8192));
        var z1 = Path.Combine(root, "l1.zar");
        var z19 = Path.Combine(root, "l19.zar");
        ZarPipeline.Pack(src, z1, new ZarPipelineOptions { Level = 1, Dictionary = dict });
        ZarPipeline.Pack(src, z19, new ZarPipelineOptions { Level = 19, Dictionary = dict });
        Assert.NotEqual(File.ReadAllBytes(z1), File.ReadAllBytes(z19));

        var opt = new ZarPipelineOptions { Dictionary = dict };
        ZarPipeline.Extract(z1, Path.Combine(root, "o1"), opt);
        ZarPipeline.Extract(z19, Path.Combine(root, "o19"), opt);
        foreach (var file in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(file);
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(root, "o1", name)),
                File.ReadAllBytes(Path.Combine(root, "o19", name)));
        }
    }
}
