using ZArchiveSharp.Pipeline;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Tests for <see cref="ZarchiveCli"/> (the callable
/// <c>zarchive.exe input [output]</c> contract) and <see cref="ProcessRunner"/>
/// tool launching).
/// </summary>
public sealed class PipelineCliTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

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

    private sealed class LogSink
    {
        public readonly List<string> Lines = [];
        private readonly Lock _gate = new();

        public void Log(string line)
        {
            lock (_gate)
            {
                Lines.Add(line);
            }
        }
    }

    // ------------------------------------------------------------------
    // zarchive.exe contract
    // ------------------------------------------------------------------

    [Fact]
    public void Cli_NoArgs_PrintsUsageAndOk()
    {
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("zarchive.exe input_path", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_TooManyArgs_ReturnsBadUsage()
    {
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.BadUsage, ZarchiveCli.Run(["a", "b", "c"], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("Too many paths", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_PackDir_DefaultNameAndLog()
    {
        var root = NewTempDir("cli_pack");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src], log: sink.Log));

        Assert.True(File.Exists(Path.Combine(root, "game.zar")));
        Assert.Contains(sink.Lines, l => l.Contains("Outputting to:", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains("Adding a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_PackRefusesExisting_ReturnsRefused()
    {
        var root = NewTempDir("cli_refuse");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(root, "game.zar"), "\t\t\t"u8);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Refused, ZarchiveCli.Run([src], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("already exists", StringComparison.Ordinal));
        Assert.Equal("\t\t\t"u8.ToArray(), File.ReadAllBytes(Path.Combine(root, "game.zar")));
    }

    [Fact]
    public void Cli_Pack_ShallowCopy_PreservesNameOrder()
    {
        // NameOrder must survive the option copy the CLI layer makes before
        // handing options to the packer; dropping it silently changes the
        // name table (and the archive bytes) versus the library default.
        var root = NewTempDir("cli_nameorder");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "aaaa");
        File.WriteAllText(Path.Combine(src, "b.txt"), "bbbb");
        string[] order = ["b.txt", "a.txt"];

        var expected = Path.Combine(root, "expected.zar");
        ZarPipeline.Pack(src, expected, new ZarPipelineOptions { NameOrder = order });
        var viaCli = Path.Combine(root, "via_cli.zar");
        Assert.Equal(ZarchiveCli.Ok,
            ZarchiveCli.Run([src, viaCli], new ZarPipelineOptions { NameOrder = order }));
        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(viaCli));
    }

    [Fact]
    public void Cli_ExtractIoFailure_ReturnsExtractionFailed()
    {
        // An unopenable extract destination is an extraction failure (-12),
        // not a pack failure (-13).
        var root = NewTempDir("cli_extractio");
        var zar = Path.Combine(root, "input.zar");
        using (var fs = File.Create(zar))
        using (var writer = new ZArchiveWriter(fs))
        {
            Assert.True(writer.StartNewFile("hello.txt"));
            writer.AppendData("hi"u8);
            writer.Finalize();
        }

        // Occupy the output file's path with a directory.
        var dest = Path.Combine(root, "dest");
        Directory.CreateDirectory(Path.Combine(dest, "hello.txt"));

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.ExtractionFailed, ZarchiveCli.Run([zar, dest], log: sink.Log));
    }

    [Fact]
    public async Task Runner_HungChildAfterStdoutClose_CancellationKillsIt()
    {
        var python = FindPython();
        if (python is null)
        {
            return; // no Python on this host: nothing to run.
        }

        // The child closes stdout then heartbeats into a marker file for up
        // to 30 s. After cancellation we prove it really stopped writing:
        // asserting only the parent's return time would let a leaked child
        // pass.
        var marker = Path.Combine(Path.GetTempPath(), "zar_hung_" + Guid.NewGuid().ToString("N") + ".txt");
        var script = "import os,time,sys; os.close(1); f=open(sys.argv[1],'a'); " +
                     "[(f.write('x'), f.flush(), time.sleep(0.2)) for _ in range(150)]";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var runner = Task.Run(() => Assert.ThrowsAny<OperationCanceledException>(() => ProcessRunner.Run(
                python,
                "-c \"" + script + "\" \"" + marker + "\"",
                // ReSharper disable once AccessToDisposedClosure
                cancellationToken: cts.Token)));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(10) &&
                   (!File.Exists(marker) || new FileInfo(marker).Length == 0))
            {
                Thread.Sleep(50);
            }

            Assert.True(File.Exists(marker) && new FileInfo(marker).Length > 0,
                "The child never started heartbeating.");
            cts.Cancel();
            await runner;

            // A killed child stops appending; a leaked one would add ~5
            // heartbeats during the observation second.
            var stopped = new FileInfo(marker).Length;
            Thread.Sleep(TimeSpan.FromSeconds(1));
            Assert.Equal(stopped, new FileInfo(marker).Length);
        }
        finally
        {
            cts.Cancel();
            try
            {
                File.Delete(marker);
            }
            catch (IOException)
            {
                // Best effort; the child's last write may race the delete.
            }
        }
    }

    [Fact]
    public void Runner_LateStderrFailure_ReportsLastLine()
    {
        var python = FindPython();
        if (python is null)
        {
            return; // no Python on this host: nothing to run.
        }

        var ex = Assert.Throws<InvalidOperationException>(() => ProcessRunner.Run(
            python,
            "-c \"import os,sys,time; os.close(1); time.sleep(0.3); " +
            "sys.stderr.write('late-stderr-boom'); sys.stderr.flush(); sys.exit(7)\""));
        Assert.Contains("late-stderr-boom", ex.Message, StringComparison.Ordinal);
    }

    private static string? FindPython()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { "python.exe", "python3.exe" }
            : new[] { "python3", "python" };
        foreach (var name in names)
        {
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (dir.Length == 0)
                {
                    continue;
                }

                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    [Fact]
    public void Cli_PackForcesFailPolicyOption_StillRefusesExisting()
    {
        // ZarchiveCli is the refuse-overwrite contract: even an Overwrite
        // policy in options is forced to Fail;
        // policy-aware packing lives in ZarPipeline.Pack.
        var root = NewTempDir("cli_policy");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllBytes(Path.Combine(root, "game.zar"), "\t\t\t"u8);

        var sink = new LogSink();
        var options = new ZarPipelineOptions { CollisionPolicy = ZarCollisionPolicy.Overwrite };
        Assert.Equal(ZarchiveCli.Refused, ZarchiveCli.Run([src], options, log: sink.Log));
        Assert.Equal("\t\t\t"u8.ToArray(), File.ReadAllBytes(Path.Combine(root, "game.zar")));
    }

    [Fact]
    public void Cli_PackOutputIsDirectory_ReturnsNotFound()
    {
        var root = NewTempDir("cli_outdir");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var clash = Directory.CreateDirectory(Path.Combine(root, "clash")).FullName;

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.NotFound, ZarchiveCli.Run([src, clash], log: sink.Log));
    }

    [Fact]
    public void Cli_InvalidInput_ReturnsBadUsage()
    {
        var root = NewTempDir("cli_bad");
        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.BadUsage,
            ZarchiveCli.Run([Path.Combine(root, "missing")], log: sink.Log));
    }

    [Fact]
    public void Cli_Extract_DefaultDirAndFileLog()
    {
        var root = NewTempDir("cli_ext");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "game.zar");
        ZarPipeline.Pack(src, zar);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zar], log: sink.Log));

        var outDir = Path.Combine(root, "game_extracted");
        Assert.Equal("hello", File.ReadAllText(Path.Combine(outDir, "a.txt")));
        Assert.Contains(sink.Lines, l => l.Contains("Extracting to:", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, l => l.Contains("a.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_ExtractOutputIsFile_ReturnsOutputNotDirectory()
    {
        var root = NewTempDir("cli_extbad");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "game.zar");
        ZarPipeline.Pack(src, zar);
        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "x");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.OutputNotDirectory, ZarchiveCli.Run([zar, blocker], log: sink.Log));
    }

    [Fact]
    public void Cli_RoundTrip_ExplicitPaths()
    {
        var root = NewTempDir("cli_rt");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllBytes(Path.Combine(src, "a.bin"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        var zar = Path.Combine(root, "custom.zar");
        var dest = Path.Combine(root, "restored");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src, zar], log: sink.Log));
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zar, dest], log: sink.Log));
        Assert.Equal(File.ReadAllBytes(Path.Combine(src, "a.bin")), File.ReadAllBytes(Path.Combine(dest, "a.bin")));
    }

    [Fact]
    public void Cli_PackFailure_DeletesIncompleteOutput()
    {
        var root = NewTempDir("cli_incomplete");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var zar = Path.Combine(root, "out.zar");

        var sink = new LogSink();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // A cancelled pack surfaces the cancellation (not a masked exit code).
        Assert.Throws<OperationCanceledException>(() =>
            ZarchiveCli.Run([src, zar], log: sink.Log, cancellationToken: cts.Token));
        Assert.False(File.Exists(zar));
    }

    [Fact]
    public void Cli_ExtractGarbage_ReturnsRefused()
    {
        var root = NewTempDir("cli_garbage");
        var zar = Path.Combine(root, "garbage.zar");
        File.WriteAllBytes(zar, [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4]);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Refused, ZarchiveCli.Run([zar], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("Failed to open ZArchive", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_PackLockedInput_ReturnsInputNotReadable()
    {
        var root = NewTempDir("cli_locked");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var victim = Path.Combine(src, "a.txt");
        File.WriteAllText(victim, "hello");
        var zar = Path.Combine(root, "out.zar");

        var sink = new LogSink();
        using var lockStream = new FileStream(victim, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(ZarchiveCli.InputNotReadable, ZarchiveCli.Run([src, zar], log: sink.Log));
        Assert.Contains(sink.Lines, l => l.Contains("Failed to open input file", StringComparison.Ordinal));
        Assert.False(File.Exists(zar));
    }

    [Fact]
    public void Cli_Extract_LogsNativeEntryLines()
    {
        var root = NewTempDir("cli_extlog");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
        File.WriteAllBytes(Path.Combine(sub, "b.bin"), [1, 2, 3]);
        var zar = Path.Combine(root, "game.zar");
        ZarPipeline.Pack(src, zar);

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zar, Path.Combine(root, "out")], log: sink.Log));

        // main.cpp prints srcPath/name per entry (leading "/" at the top
        // level), directories included, in preorder — kept byte-identical.
        var entryLines = sink.Lines.Where(l => l.StartsWith('/') || l.Contains('/')).ToList();
        Assert.Equal(["/a.txt", "/sub", "sub/b.bin"], entryLines);
    }

    [Fact]
    public void Cli_Pack_AddingUsesOsSeparators()
    {
        var root = NewTempDir("cli_addsep");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(src, "sub")).FullName;
        File.WriteAllText(Path.Combine(sub, "b.txt"), "hello");

        var sink = new LogSink();
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src, Path.Combine(root, "game.zar")], log: sink.Log));

        var expected = "Adding sub" + Path.DirectorySeparatorChar + "b.txt";
        Assert.Contains(sink.Lines, l => string.Equals(l, expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_Pack_HonorsLevelOption()
    {
        // P0-2 lock-in: ZarPipelineOptions.Level must reach the blocks on the
        // plain ZarchiveCli pack path (it once built its own options).
        var root = NewTempDir("cli_level");
        var src = Directory.CreateDirectory(Path.Combine(root, "src")).FullName;
        var text = new char[200_000];
        const string alphabet = "the quick brown fox jumps over the lazy dog 0123456789\n";
        for (var i = 0; i < text.Length; i++)
        {
            text[i] = alphabet[((i * 31) + (i / 7)) % alphabet.Length];
        }

        File.WriteAllText(Path.Combine(src, "big.txt"), new string(text));

        var sink = new LogSink();
        var zarL1 = Path.Combine(root, "l1.zar");
        var zarL19 = Path.Combine(root, "l19.zar");
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src, zarL1],
            new ZarPipelineOptions { Level = 1 }, log: sink.Log));
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([src, zarL19],
            new ZarPipelineOptions { Level = 19 }, log: sink.Log));

        var bytesL1 = File.ReadAllBytes(zarL1);
        var bytesL19 = File.ReadAllBytes(zarL19);
        Assert.NotEqual(bytesL1, bytesL19);

        // Same level through the engine directly must be byte-identical to
        // the CLI path (same call sequence, same options plumbing).
        var zarDirect = Path.Combine(root, "direct.zar");
        ZarPipeline.Pack(src, zarDirect, new ZarPipelineOptions { Level = 1 });
        Assert.Equal(bytesL1, File.ReadAllBytes(zarDirect));

        // Both levels must still round-trip cleanly (different, not corrupt).
        var outL1 = Path.Combine(root, "out_l1");
        var outL19 = Path.Combine(root, "out_l19");
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zarL1, outL1], log: sink.Log));
        Assert.Equal(ZarchiveCli.Ok, ZarchiveCli.Run([zarL19, outL19], log: sink.Log));
        Assert.Equal(
            File.ReadAllText(Path.Combine(outL1, "big.txt")),
            File.ReadAllText(Path.Combine(outL19, "big.txt")));
    }

    [Fact]
    public void Engine_PackDuplicateEntries_ThrowsEntryCreateFault()
    {
        var root = NewTempDir("cli_dupe");
        var zar = Path.Combine(root, "out.zar");
        var payload = new byte[] { 1, 2, 3 };
        var entries = new List<ZarPackEntry>
        {
            new()
            {
                RelativePath = "dup.txt", IsDirectory = false, Length = payload.Length,
                OpenRead = () => new MemoryStream(payload, writable: false)
            },
            new()
            {
                RelativePath = "dup.txt", IsDirectory = false, Length = payload.Length,
                OpenRead = () => new MemoryStream(payload, writable: false)
            },
        };

        var ex = Assert.Throws<ZarEntryCreateException>(() =>
            ZarPackEngine.PackEntries(entries, root, zar, new ZarPipelineOptions()));
        Assert.Contains("Failed to create archive file", ex.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(zar));
    }

    // ------------------------------------------------------------------
    // ProcessRunner
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("42%", 0.42)]
    [InlineData("7z 100%", 1.0)]
    [InlineData("  7% done", 0.07)]
    [InlineData("1%2%", 0.01)]
    [InlineData("150%", 1.0)]
    public void Runner_ParseProgress_FirstMatchWins(string line, double expected)
    {
        var actual = ProcessRunner.TryParseProgressLine(line);
        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 9);
    }

    [Theory]
    [InlineData("Adding foo/bar.txt")]
    [InlineData("")]
    [InlineData("no digits here")]
    [InlineData("% dangling")]
    public void Runner_ParseProgress_NoMatch_ReturnsNull(string line)
    {
        Assert.Null(ProcessRunner.TryParseProgressLine(line));
    }

    [Fact]
    public void Runner_ExitMapping_AcceptsZeroAndOne()
    {
        ProcessRunner.ThrowIfFailed(0, null, "tool");
        ProcessRunner.ThrowIfFailed(1, "warning", "tool");
        var ex = Assert.Throws<InvalidOperationException>(() => ProcessRunner.ThrowIfFailed(2, "boom", "tool"));
        Assert.Contains("2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_MissingBinary_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => ProcessRunner.Run("definitely-not-a-tool-xyz-123"));
    }

    [Fact]
    public void Runner_PreCancelled_DoesNotRun()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                ProcessRunner.Run("dotnet", "--version", cancellationToken: cts.Token));
        }
        catch (FileNotFoundException)
        {
            // dotnet itself missing: environment cannot run tools; nothing to assert.
        }
    }

    [Fact]
    public void Runner_Smoke_DotnetVersion_ExitZero()
    {
        double? last = null;
        var progress = new SmokeProgress(v => last = v);
        ProcessRunner.ProcessResult result;
        try
        {
            result = ProcessRunner.Run("dotnet", "--version", progress: progress);
        }
        catch (FileNotFoundException)
        {
            // dotnet itself missing: environment cannot run tools; nothing to assert.
            return;
        }

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrEmpty(result.LastLine));
        Assert.Null(last);
    }

    private sealed class SmokeProgress(Action<double> action) : IProgress<double>
    {
        private readonly Action<double> _action = action;

        public void Report(double value)
        {
            _action(value);
        }
    }
}