using System.Diagnostics;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Black-box CLI tests for global option validation: value-taking options
/// must not swallow the next flag or silently ignore bad values, <c>--iso</c>
/// must not combine with <c>--batch</c>, and globally consumed flags must
/// still honor the per-verb rules of <c>zar zstd</c> / <c>zar seekable</c>.
/// </summary>
public sealed class CliOptionValidationTests : IDisposable
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
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // ignored: best-effort temp cleanup.
            }
        }
    }

    private string NewTempDir(string prefix)
    {
        var dir = RedumpIsoTests.NewTempDir(prefix);
        _tempDirs.Add(dir);
        return dir;
    }

    private static string? Cli => RedumpIsoTests.FindCli();

    private static byte[] Payload(int size, byte value)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, value);
        return bytes;
    }

    [Fact]
    public void Iso_WithBatch_IsUsageError()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_iobatch");
        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["--iso", "missing.iso", "out.zar", "--batch"]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--iso cannot be combined with --batch", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("abc")]
    public void Level_Invalid_IsUsageError(string raw)
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_level");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["--level", raw, src, Path.Combine(work, "out.zar")]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--level", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "out.zar")), "A bad --level must not pack anything.");
    }

    [Fact]
    public void Jobs_FollowingOption_IsUsageError()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_jobsflag");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["--jobs", "--quiet", src, Path.Combine(work, "out.zar")]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--jobs", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "out.zar")), "The following flag must not be consumed as a value.");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    public void Jobs_Invalid_IsUsageError(string raw)
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_jobs");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["--jobs", raw, src, Path.Combine(work, "out.zar")]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--jobs", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputAndIso_MissingValues_AreUsageErrors()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_missing");

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, ["-o"]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("missing value for -o", stderr, StringComparison.Ordinal);

        (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, ["--iso"]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("missing value for --iso", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekableList_Check_IsUsageError()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_seekcheck");
        var seekable = CreateSeekable(cli, work, "in.zst");
        if (seekable is null)
        {
            return;
        }

        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["seekable", "list", "--check", seekable]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--check", stderr, StringComparison.Ordinal);
        Assert.Contains("seekable compress", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekableList_StdoutAndLevel_AreUsageErrors()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_seeklist");
        var seekable = CreateSeekable(cli, work, "in.zst");
        if (seekable is null)
        {
            return;
        }

        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["seekable", "list", "--stdout", seekable]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--stdout", stderr, StringComparison.Ordinal);

        (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["seekable", "list", "--level", "5", seekable]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--level", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SeekableCompress_CheckAndLevel_StillWork()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_seekok");
        var input = Path.Combine(work, "in.bin");
        File.WriteAllBytes(input, Payload(4096, 7));
        var output = Path.Combine(work, "out.zst");
        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            ["seekable", "compress", "--check", "-l", "5", input, output]);
        if (!started)
        {
            return;
        }

        Assert.True(exit == 0, $"Seekable compress failed (exit {exit}): {stderr}");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void ZstdDecompress_Check_IsUsageError()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_zstdcheck");
        var input = Path.Combine(work, "in.bin");
        File.WriteAllBytes(input, Payload(4096, 9));
        var compressed = Path.Combine(work, "in.zst");
        var (started, exit, _, stderr) =
            RedumpIsoTests.TryRunCli(cli, work, ["zstd", "-c", input, compressed]);
        if (!started || exit != 0)
        {
            return;
        }

        (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            ["zstd", "-d", "--check", compressed, Path.Combine(work, "out.bin")]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("--check", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(work, "out.bin")), "A rejected run must not decompress.");
    }

    [Fact]
    public void NoCompress_WithMissingDict_StillPacks()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_nodict");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "data");
        var output = Path.Combine(work, "out.zar");

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            [src, output, "--no-compress", "--dict", Path.Combine(work, "missing.dict")]);
        if (!started)
        {
            return;
        }

        // --no-compress ignores --dict (docs): a missing dictionary must not
        // fail a raw pack.
        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void UnhandledException_ReportsExceptionDetails()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        // An empty output path reaches FileStream and throws ArgumentException
        // (not one of the mapped I/O faults), so it escapes to Main's fatal
        // handler: an empty argument exercises it deterministically.
        var work = NewTempDir("opt_fatal");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "data");

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, [src, ""]);
        if (!started)
        {
            return;
        }

        Assert.Equal(Pipeline.ZarchiveCli.PackFailed, exit);
        Assert.Contains("Unhandled exception.", stderr, StringComparison.Ordinal);
        Assert.Contains("ArgumentException", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownOption_IsUsageError()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_unknown");
        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, ["--bogus"]);
        if (!started)
        {
            return;
        }

        // Pre-fix the token became the input path ("not a valid file or
        // directory") instead of a usage error.
        Assert.Equal(-1, exit);
        Assert.Contains("unknown option", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EndOfOptions_AllowsDashedPath()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_dashdash");
        var src = Directory.CreateDirectory(Path.Combine(work, "-dashed")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "data");
        var output = Path.Combine(work, "out.zar");

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, ["--", "-dashed", output]);
        if (!started)
        {
            return;
        }

        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Help_DocumentsTelemetrySeekableStdoutAndTerminator()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_help");
        var (started, exit, stdout, _) = RedumpIsoTests.TryRunCli(cli, work, ["--help"]);
        if (!started)
        {
            return;
        }

        Assert.Equal(0, exit);
        Assert.Contains("--no-telemetry", stdout, StringComparison.Ordinal);
        Assert.Contains("ZAR_BUG_REPORT", stdout, StringComparison.Ordinal);
        Assert.Contains("Use '--'", stdout, StringComparison.Ordinal);
        // Seekable also accepts -c/--stdout, so the old "only with zar zstd"
        // wording was wrong.
        Assert.Contains("'zar zstd' and 'zar seekable'", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("only with 'zar zstd'", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NoTelemetry_IsAcceptedOnThePackPath()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_notelemetry");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "data");
        var output = Path.Combine(work, "out.zar");

        // Pre-fix --no-telemetry became the input path (too many paths).
        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work, ["--no-telemetry", src, output]);
        if (!started)
        {
            return;
        }

        Assert.True(exit == 0, $"exit {exit}: {stderr}");
        Assert.True(File.Exists(output));
    }

    [Fact]
    public void Batch_UnreadableInputDirectory_Fails()
    {
        var cli = Cli;
        if (cli is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var work = NewTempDir("opt_acllist");
        var locked = Directory.CreateDirectory(Path.Combine(work, "locked")).FullName;
        File.WriteAllText(Path.Combine(locked, "a.txt"), "data");
        if (!SetDirectoryDenyAce(locked, deny: true))
        {
            return; // ACLs unavailable here: vacuous pass.
        }

        try
        {
            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                ["--batch", locked, Path.Combine(work, "outdir")]);
            if (!started)
            {
                return;
            }

            // Pre-fix: empty list + "No processable files found." + exit 0.
            Assert.Equal(Pipeline.ZarchiveCli.PackFailed, exit);
            Assert.Contains("cannot list input directory", stderr, StringComparison.Ordinal);
        }
        finally
        {
            SetDirectoryDenyAce(locked, deny: false);
        }
    }

    [Fact]
    public void Dictionary_DirectoryPath_ReportsAccessDenied()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_dictdir");
        var src = Directory.CreateDirectory(Path.Combine(work, "src")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "data");
        var dictDir = Directory.CreateDirectory(Path.Combine(work, "dictdir")).FullName;

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            [src, Path.Combine(work, "out.zar"), "--dict", dictDir]);
        if (!started)
        {
            return;
        }

        // Reading a directory is UnauthorizedAccessException, not a missing
        // file: pre-fix both printed "dictionary file not found".
        Assert.Equal(-1, exit);
        Assert.Contains("access denied", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dictionary file not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZstdDictionary_DirectoryPath_ReportsAccessDenied()
    {
        var cli = Cli;
        if (cli is null)
        {
            return;
        }

        var work = NewTempDir("opt_zstddictdir");
        var input = Path.Combine(work, "in.bin");
        File.WriteAllBytes(input, Payload(2048, 5));
        var dictDir = Directory.CreateDirectory(Path.Combine(work, "dictdir")).FullName;

        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            ["zstd", "-c", input, Path.Combine(work, "in.zst"), "--dict", dictDir]);
        if (!started)
        {
            return;
        }

        Assert.Equal(-1, exit);
        Assert.Contains("access denied", stderr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dictionary file not found", stderr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SetDirectoryDenyAce(string directory, bool deny)
    {
        try
        {
            var psi = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(directory);
            psi.ArgumentList.Add(deny ? "/deny" : "/remove:d");
            psi.ArgumentList.Add(Environment.UserName + ":(RD)");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            return proc.WaitForExit(15000) && proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? CreateSeekable(string cli, string work, string name)
    {
        var input = Path.Combine(work, "seekable_in.bin");
        File.WriteAllBytes(input, Payload(8192, 3));
        var output = Path.Combine(work, name);
        var (started, exit, _, _) = RedumpIsoTests.TryRunCli(cli, work,
            ["seekable", "compress", input, output]);
        if (!started || exit != 0)
        {
            return null;
        }

        return output;
    }
}