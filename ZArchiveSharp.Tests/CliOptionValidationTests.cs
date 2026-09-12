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

    private static string? CreateSeekable(string cli, string work, string name)
    {
        var input = Path.Combine(work, "seekable_in.bin");
        File.WriteAllBytes(input, Payload(8192, 3));
        var output = Path.Combine(work, name);
        var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
            ["seekable", "compress", input, output]);
        if (!started || exit != 0)
        {
            return null;
        }

        return output;
    }
}
