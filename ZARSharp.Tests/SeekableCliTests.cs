using ZARSharp.Pipeline;
using ZARSharp.Seekable;

namespace ZARSharp.Tests;

/// <summary>
/// Tests for <see cref="SeekableCli"/> (<c>zar seekable compress|decompress|list</c>:
/// arg parsing, ByteValue sizes, file round-trips, slices, tables, exit codes).
/// </summary>
public sealed class SeekableCliTests : IDisposable
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

    private Task<int> RunSeekableAsync(SeekableCli.SeekableJob job, MemoryStream stdin, MemoryStream stdout, CancellationToken ct = default)
    {
        _log.Clear();
        _errors.Clear();
        return SeekableCli.RunAsync(job, stdin, stdout, _log.Add, _errors.Add, ct);
    }

    private static bool Parsed(string[] args, out SeekableCli.SeekableJob? job, out string? error) =>
        SeekableCli.TryParse(args, out job, out error);

    private static SeekableCli.SeekableJob CompressJob(
        string? input = null, string? output = null, int frameSize = 16 * 1024, bool quiet = true) => new()
        {
            Command = SeekableCli.SeekableCommand.Compress,
            InputPath = input,
            OutputPath = output,
            FrameSize = frameSize,
            Quiet = quiet,
        };

    // ------------------------------------------------------------------
    // Arg parsing: verbs and aliases
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_NoVerb_Rejects()
    {
        Assert.False(Parsed([], out _, out string? error));
        Assert.Contains("compress", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_BadVerb_Rejects()
    {
        Assert.False(Parsed(["frobnicate"], out _, out string? error));
        Assert.Contains("frobnicate", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("compress", SeekableCli.SeekableCommand.Compress)]
    [InlineData("c", SeekableCli.SeekableCommand.Compress)]
    [InlineData("decompress", SeekableCli.SeekableCommand.Decompress)]
    [InlineData("d", SeekableCli.SeekableCommand.Decompress)]
    [InlineData("list", SeekableCli.SeekableCommand.List)]
    [InlineData("l", SeekableCli.SeekableCommand.List)]
    public void Parse_VerbAliases(string verb, SeekableCli.SeekableCommand expected)
    {
        Assert.True(Parsed([verb, "--help"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal(expected, job.Command);
        Assert.True(job.ShowHelp);
    }

    [Fact]
    public void Parse_BareHelp_ShortCircuits()
    {
        Assert.True(Parsed(["--help"], out var job, out _));
        Assert.NotNull(job);
        Assert.True(job.ShowHelp);
    }

    [Fact]
    public void Parse_CompressDefaults()
    {
        Assert.True(Parsed(["compress"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal(3, job.Level);
        Assert.Equal(2 * 1024 * 1024, job.FrameSize);
        Assert.Equal(SeekableFrameSizePolicy.Uncompressed, job.Policy);
        Assert.True(job.Checksum);
        Assert.False(job.Force);
        Assert.False(job.Quiet);
        Assert.Null(job.InputPath);
        Assert.Null(job.OutputPath);
    }

    [Fact]
    public void Parse_CompressDerivesOutput()
    {
        Assert.True(Parsed(["compress", "in.bin"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal("in.bin", job.InputPath);
        Assert.Equal("in.bin.zst", job.OutputPath);
    }

    [Fact]
    public void Parse_CompressStdout_DoesNotDerive()
    {
        Assert.True(Parsed(["compress", "in.bin", "--stdout"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal("in.bin", job.InputPath);
        Assert.Null(job.OutputPath);
    }

    [Fact]
    public void Parse_InheritedDefaults_Overridable()
    {
        Assert.True(SeekableCli.TryParse(["compress"], out var job, out _,
            defaultLevel: 9, defaultChecksum: false, defaultQuiet: true, defaultStdout: false));
        Assert.NotNull(job);
        Assert.Equal(9, job.Level);
        Assert.False(job.Checksum);
        Assert.True(job.Quiet);

        Assert.True(SeekableCli.TryParse(["compress", "-l", "5", "--checksum"], out var over, out _,
            defaultLevel: 9, defaultChecksum: false));
        Assert.NotNull(over);
        Assert.Equal(5, over.Level);
        Assert.True(over.Checksum);
    }

    [Fact]
    public void Parse_BadDefaultLevel_Rejects()
    {
        Assert.False(SeekableCli.TryParse(["compress"], out _, out _, defaultLevel: 99));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("23")]
    [InlineData("abc")]
    public void Parse_BadLevel_Rejects(string level)
    {
        Assert.False(Parsed(["compress", "-l", level], out _, out _));
    }

    [Fact]
    public void Parse_MissingValues_Reject()
    {
        foreach (var flag in new[] { "-l", "-s", "--frame-size-policy", "--seek-table-file", "--from", "--to", "--from-frame", "--to-frame", "--num-frames", "--seek-table-format" })
        {
            string verb = flag is "--num-frames" or "--seek-table-format" ? "list"
                : flag is "--from" or "--to" ? "decompress" : "compress";
            Assert.False(Parsed([verb, flag], out _, out _));
        }
    }

    // ------------------------------------------------------------------
    // ByteValue sizes
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("10", 10UL)]
    [InlineData("10B", 10UL)]
    [InlineData("10 B", 10UL)]
    [InlineData("10   B", 10UL)]
    [InlineData("10K", 10UL * 1024)]
    [InlineData("10 K", 10UL * 1024)]
    [InlineData("10 kib", 10UL * 1024)]
    [InlineData("10 KIB", 10UL * 1024)]
    [InlineData("10M", 10UL * 1024 * 1024)]
    [InlineData("10 mib", 10UL * 1024 * 1024)]
    [InlineData("2G", 2UL * 1024 * 1024 * 1024)]
    [InlineData("2 gib", 2UL * 1024 * 1024 * 1024)]
    public void ByteSize_Parses(string raw, ulong expected)
    {
        Assert.True(SeekableCli.TryParseByteSize(raw, out ulong size, out _));
        Assert.Equal(expected, size);
    }

    [Theory]
    [InlineData("10 X")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc B")]
    [InlineData("10.5M")]
    [InlineData("18446744073709551615G")]
    public void ByteSize_Rejects(string raw)
    {
        Assert.False(SeekableCli.TryParseByteSize(raw, out _, out _));
    }

    [Fact]
    public void Parse_FrameSizeRange()
    {
        Assert.True(Parsed(["compress", "-s", "64K"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal(64 * 1024, job.FrameSize);

        Assert.False(Parsed(["compress", "-s", "0"], out _, out _));
        Assert.False(Parsed(["compress", "-s", "2G"], out _, out _));
    }

    [Theory]
    [InlineData("compressed", SeekableFrameSizePolicy.Compressed)]
    [InlineData("COMPRESSED", SeekableFrameSizePolicy.Compressed)]
    [InlineData("uncompressed", SeekableFrameSizePolicy.Uncompressed)]
    public void Parse_Policy(string raw, SeekableFrameSizePolicy expected)
    {
        Assert.True(Parsed(["compress", "--frame-size-policy", raw], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal(expected, job.Policy);
        Assert.False(Parsed(["compress", "--frame-size-policy", "sideways"], out _, out _));
    }

    [Fact]
    public void Parse_ChecksumLastWins()
    {
        Assert.True(Parsed(["compress", "--no-checksum", "--checksum"], out var on, out _));
        Assert.NotNull(on);
        Assert.True(on.Checksum);

        Assert.True(Parsed(["compress", "--check", "--no-check"], out var off, out _));
        Assert.NotNull(off);
        Assert.False(off.Checksum);
    }

    [Theory]
    [InlineData("--frobnicate")]
    [InlineData("-x")]
    public void Parse_UnknownOption_Rejects(string flag)
    {
        Assert.False(Parsed(["compress", flag], out _, out _));
    }

    [Theory]
    [InlineData("list", "-l", "3")]
    [InlineData("decompress", "-l", "3")]
    [InlineData("decompress", "-s", "64K")]
    [InlineData("compress", "--from", "5")]
    [InlineData("compress", "--to", "5")]
    [InlineData("compress", "--from-frame", "0")]
    [InlineData("compress", "--to-frame", "0")]
    [InlineData("compress", "--num-frames", "2")]
    [InlineData("compress", "--detail")]
    [InlineData("compress", "--seek-table-format", "head")]
    [InlineData("list", "-f")]
    [InlineData("list", "--seek-table-file", "t.head")]
    [InlineData("list", "--stdout")]
    [InlineData("decompress", "--num-frames", "2")]
    [InlineData("decompress", "--detail")]
    [InlineData("decompress", "--seek-table-format", "head")]
    public void Parse_VerbMismatch_Rejects(string verb, string flag, string? value = null)
    {
        string[] args = value is null ? [verb, flag] : [verb, flag, value];
        Assert.False(Parsed(args, out _, out _));
    }

    [Fact]
    public void Parse_DecompressConflicts_Reject()
    {
        Assert.False(Parsed(["decompress", "--from", "1", "--from-frame", "0"], out _, out _));
        Assert.False(Parsed(["decompress", "--to", "5", "--to-frame", "0"], out _, out _));
    }

    [Fact]
    public void Parse_ListConflicts_Reject()
    {
        Assert.False(Parsed(["list", "--to-frame", "1", "--num-frames", "2"], out _, out _));
        Assert.False(Parsed(["list"], out _, out _));
        Assert.False(Parsed(["list", "a", "b"], out _, out _));
        Assert.False(Parsed(["list", "a", "b", "c"], out _, out _));
    }

    [Fact]
    public void Parse_NumFramesZero_Rejects()
    {
        Assert.False(Parsed(["list", "f", "--num-frames", "0"], out _, out _));
        Assert.False(Parsed(["list", "f", "--num-frames", "abc"], out _, out _));
    }

    [Fact]
    public void Parse_EndLast_Parse()
    {
        Assert.True(Parsed(["decompress", "--to", "END"], out var toEnd, out _));
        Assert.NotNull(toEnd);
        Assert.Null(toEnd.To);

        Assert.True(Parsed(["decompress", "--to", "10K"], out var toNum, out _));
        Assert.NotNull(toNum);
        Assert.Equal(10UL * 1024, toNum.To);

        Assert.True(Parsed(["decompress", "--to-frame", "LAST"], out var last, out _));
        Assert.NotNull(last);
        Assert.True(last.ToLastFrame);

        Assert.True(Parsed(["list", "f", "--seek-table-format", "head"], out var head, out _));
        Assert.NotNull(head);
        Assert.True(head.ListHeadFormat);
        Assert.False(Parsed(["list", "f", "--seek-table-format", "bogus"], out _, out _));
    }

    [Fact]
    public void Parse_StdoutWithOutputPath_Rejects()
    {
        Assert.False(Parsed(["compress", "in", "out", "--stdout"], out _, out _));
    }

    [Fact]
    public void Parse_TooManyPaths_Rejects()
    {
        Assert.False(Parsed(["compress", "a", "b", "c"], out _, out _));
    }

    // ------------------------------------------------------------------
    // Round-trips
    // ------------------------------------------------------------------

    [Fact]
    public async Task RoundTrip_File()
    {
        var dir = NewTempDir("seekrt");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.bin.zst");
        var back = Path.Combine(dir, "back.bin");
        await File.WriteAllBytesAsync(input, CycleBytes(100_000));

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        Assert.True(File.Exists(packed));

        var table = SeekTable.ParseFoot(await File.ReadAllBytesAsync(packed));
        Assert.True(table.FrameCount > 1);

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = back,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(back));
    }

    [Fact]
    public async Task RoundTrip_DefaultName()
    {
        var dir = NewTempDir("seekname");
        var input = Path.Combine(dir, "data.bin");
        await File.WriteAllBytesAsync(input, CycleBytes(10_000));

        // Derivation happens at parse time; run the parsed job end to end.
        Assert.True(Parsed(["compress", input, "-s", "4K"], out var job, out _));
        Assert.NotNull(job);
        Assert.Equal(input + ".zst", job.OutputPath);
        job.Quiet = true;

        Assert.Equal(0, await RunSeekableAsync(job, new MemoryStream(), new MemoryStream()));
        Assert.True(File.Exists(input + ".zst"));
    }

    [Fact]
    public async Task RoundTrip_Streams()
    {
        var data = CycleBytes(50_000);
        using var packed = new MemoryStream();
        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Compress,
            FrameSize = 8192,
            Checksum = false,
            Quiet = true,
        }, new MemoryStream(data), packed));
        Assert.True(packed.Length > 0);

        packed.Position = 0;
        using var back = new MemoryStream();
        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            Quiet = true,
        }, packed, back));
        Assert.Equal(data, back.ToArray());
    }

    [Fact]
    public async Task RoundTrip_HeadMode()
    {
        var dir = NewTempDir("seekhead");
        var input = Path.Combine(dir, "data.bin");
        var framesPath = Path.Combine(dir, "frames.zst");
        var headPath = Path.Combine(dir, "frames.head");
        var back = Path.Combine(dir, "back.bin");
        await File.WriteAllBytesAsync(input, CycleBytes(60_000));

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Compress,
            InputPath = input,
            OutputPath = framesPath,
            SeekTablePath = headPath,
            FrameSize = 8192,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.True(File.Exists(framesPath));
        Assert.True(File.Exists(headPath));

        // Frames alone carry no table, so plain decompress must fail.
        Assert.Equal(-12, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = framesPath,
            OutputPath = back,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = framesPath,
            OutputPath = back,
            SeekTablePath = headPath,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(await File.ReadAllBytesAsync(input), await File.ReadAllBytesAsync(back));
    }

    [Fact]
    public async Task Decompress_Range_MatchesSlice()
    {
        var dir = NewTempDir("seekrange");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        var slicePath = Path.Combine(dir, "slice.bin");
        var data = CycleBytes(100_000);
        await File.WriteAllBytesAsync(input, data);

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        Assert.True(Parsed(["decompress", "--from", "10K", "--to", "50K"], out var job, out _));
        Assert.NotNull(job);
        job.InputPath = packed;
        job.OutputPath = slicePath;
        job.Quiet = true;

        Assert.Equal(0, await RunSeekableAsync(job, new MemoryStream(), new MemoryStream()));
        Assert.Equal(data.AsSpan(10 * 1024, 40 * 1024).ToArray(), await File.ReadAllBytesAsync(slicePath));
    }

    [Fact]
    public async Task Decompress_Frames_MatchRange()
    {
        var dir = NewTempDir("seekframes");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        var partPath = Path.Combine(dir, "part.bin");
        var data = CycleBytes(100_000);
        await File.WriteAllBytesAsync(input, data);

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        var packedBytes = await File.ReadAllBytesAsync(packed);
        var table = SeekTable.ParseFoot(packedBytes);
        Assert.True(table.FrameCount >= 3);

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = partPath,
            FromFrame = 1,
            ToLastFrame = true,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));

        long start = (long)table.FrameStartDecomp(1);
        Assert.Equal(data.AsSpan((int)start).ToArray(), await File.ReadAllBytesAsync(partPath));
    }

    [Fact]
    public async Task Decompress_EmptyRange_WritesEmpty()
    {
        var dir = NewTempDir("seekempty");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        var emptyPath = Path.Combine(dir, "empty.bin");
        await File.WriteAllBytesAsync(input, CycleBytes(20_000));

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = emptyPath,
            From = 100,
            To = 100,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(0, new FileInfo(emptyPath).Length);
    }

    [Fact]
    public async Task Decompress_FromPastTo_IsBadUsage()
    {
        var dir = NewTempDir("seekbadrange");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        await File.WriteAllBytesAsync(input, CycleBytes(20_000));

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = Path.Combine(dir, "o.bin"),
            From = 10,
            To = 5,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-1, code);
        Assert.Contains(_errors, e => e.Contains("past end", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Decompress_FrameOutOfRange_Fails()
    {
        var dir = NewTempDir("seekbadframe");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        await File.WriteAllBytesAsync(input, CycleBytes(20_000));

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = Path.Combine(dir, "o.bin"),
            FromFrame = 99,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-12, code);
    }

    [Fact]
    public async Task Decompress_PlainFile_Fails()
    {
        var dir = NewTempDir("seeknotable");
        var input = Path.Combine(dir, "plain.bin");
        await File.WriteAllBytesAsync(input, CycleBytes(1000));

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = input,
            OutputPath = Path.Combine(dir, "o.bin"),
            Quiet = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-12, code);
    }

    // ------------------------------------------------------------------
    // Missing inputs, refusals, force
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingInput_Codes()
    {
        var dir = NewTempDir("seekmissing");
        string missing = Path.Combine(dir, "nope.bin");

        Assert.Equal(-15, await RunSeekableAsync(
            CompressJob(missing, Path.Combine(dir, "o.zst")), new MemoryStream(), new MemoryStream()));
        Assert.Equal(-10, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = missing,
            OutputPath = Path.Combine(dir, "o.bin"),
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(-10, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = missing,
        }, new MemoryStream(), new MemoryStream()));
    }

    [Fact]
    public async Task RefuseOverwrite_ThenForce()
    {
        var dir = NewTempDir("seekforce");
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        await File.WriteAllBytesAsync(input, CycleBytes(20_000));

        Assert.Equal(0, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        long first = new FileInfo(packed).Length;

        Assert.Equal(-11, await RunSeekableAsync(CompressJob(input, packed), new MemoryStream(), new MemoryStream()));
        Assert.Contains(_errors, e => e.Contains("already exists", StringComparison.OrdinalIgnoreCase));

        var forced = CompressJob(input, packed);
        forced.Force = true;
        Assert.Equal(0, await RunSeekableAsync(forced, new MemoryStream(), new MemoryStream()));
        Assert.Equal(first, new FileInfo(packed).Length);
    }

    [Fact]
    public async Task FailedRun_DeletesPartialOutput()
    {
        var dir = NewTempDir("seekpartial");
        var packed = Path.Combine(dir, "data.zst");
        var back = Path.Combine(dir, "back.bin");
        await File.WriteAllBytesAsync(Path.Combine(dir, "data.bin"), CycleBytes(20_000));
        await File.WriteAllBytesAsync(packed, CycleBytes(100)); // corrupt: no seek table

        Assert.Equal(-12, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Decompress,
            InputPath = packed,
            OutputPath = back,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));
        Assert.False(File.Exists(back));
    }

    // ------------------------------------------------------------------
    // List
    // ------------------------------------------------------------------

    private async Task<string> PackThreeFramesAsync(string dir)
    {
        var input = Path.Combine(dir, "data.bin");
        var packed = Path.Combine(dir, "data.zst");
        await File.WriteAllBytesAsync(input, CycleBytes(50_000));
        Assert.Equal(0, await RunSeekableAsync(
            CompressJob(input, packed, frameSize: 8192), new MemoryStream(), new MemoryStream()));
        var table = SeekTable.ParseFoot(await File.ReadAllBytesAsync(packed));
        Assert.True(table.FrameCount >= 3);
        return packed;
    }

    [Fact]
    public async Task List_Summary()
    {
        var dir = NewTempDir("seeklist");
        var packed = await PackThreeFramesAsync(dir);

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(0, code);
        Assert.Equal(2, _log.Count);
        Assert.Contains("Frames", _log[0], StringComparison.Ordinal);
        Assert.Contains("Filename", _log[0], StringComparison.Ordinal);
        Assert.Contains(packed, _log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_QuietStillPrints()
    {
        var dir = NewTempDir("seeklistq");
        var packed = await PackThreeFramesAsync(dir);

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(0, code);
        Assert.Equal(2, _log.Count);
    }

    [Fact]
    public async Task List_Detail_Rows()
    {
        var dir = NewTempDir("seekdetail");
        var packed = await PackThreeFramesAsync(dir);
        var table = SeekTable.ParseFoot(await File.ReadAllBytesAsync(packed));

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            Detail = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(0, code);
        Assert.Contains("Frame Index", _log[0], StringComparison.Ordinal);
        Assert.Equal(table.FrameCount + 1, _log.Count);
    }

    [Fact]
    public async Task List_RangeAndCount()
    {
        var dir = NewTempDir("seekrange2");
        var packed = await PackThreeFramesAsync(dir);

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            FromFrame = 1,
            ToFrame = 1,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(2, _log.Count); // header + exactly one row
        Assert.StartsWith("1 ", _log[1], StringComparison.Ordinal);

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            NumFrames = 2,
        }, new MemoryStream(), new MemoryStream()));
        Assert.Equal(3, _log.Count);
    }

    [Fact]
    public async Task List_HeadFormat()
    {
        var dir = NewTempDir("seeklisthead");
        var input = Path.Combine(dir, "data.bin");
        var framesPath = Path.Combine(dir, "frames.zst");
        var headPath = Path.Combine(dir, "frames.head");
        await File.WriteAllBytesAsync(input, CycleBytes(30_000));

        Assert.Equal(0, await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.Compress,
            InputPath = input,
            OutputPath = framesPath,
            SeekTablePath = headPath,
            FrameSize = 8192,
            Quiet = true,
        }, new MemoryStream(), new MemoryStream()));

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = headPath,
            ListHeadFormat = true,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(0, code);
        Assert.Equal(2, _log.Count);
        Assert.Contains(headPath, _log[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_StartPastEnd_IsBadUsage()
    {
        var dir = NewTempDir("seeklistbad");
        var packed = await PackThreeFramesAsync(dir);

        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            FromFrame = 2,
            ToFrame = 1,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-1, code);
    }

    [Fact]
    public async Task List_OutOfRange_Fails()
    {
        var dir = NewTempDir("seeklistoor");
        var packed = await PackThreeFramesAsync(dir);

        // End past the last frame is a decode-time range failure.
        int code = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            ToFrame = 99,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-12, code);

        // Start past the default end trips the oracle's start>end guard first.
        int usage = await RunSeekableAsync(new SeekableCli.SeekableJob
        {
            Command = SeekableCli.SeekableCommand.List,
            InputPath = packed,
            FromFrame = 99,
        }, new MemoryStream(), new MemoryStream());
        Assert.Equal(-1, usage);
    }
}
