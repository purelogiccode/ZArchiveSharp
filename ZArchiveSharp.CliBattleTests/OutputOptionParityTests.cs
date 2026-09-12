namespace ZArchiveSharp.CliBattleTests;

/// <summary>
/// Regression tests for the zar positional contract extensions (-o/--output,
/// --iso): extras must fail usage with the oracle-identical message, never
/// silently drop. Oracle-free: exercises only the CLI under test.
/// </summary>
public sealed class OutputOptionParityTests : IDisposable
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
        var dir = Path.Combine(Path.GetTempPath(), "zar_battle",
            prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Fact]
    public void OutputOption_WithExtraPositional_FailsTooManyPaths()
    {
        var root = NewTempDir("o_extra");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");
        var outZar = Path.Combine(root, "out.zar");

        var mine = CliRunner.RunMine(src, "-o", outZar, "extra");
        Assert.Equal(-1, mine.ExitCode);
        Assert.Contains("Too many paths specified", mine.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(outZar), "Refused pack must not create output.");
    }

    [Fact]
    public void OutputOption_WithPositionalOutput_FailsTooManyPaths()
    {
        var root = NewTempDir("o_dup");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");

        var mine = CliRunner.RunMine(
            src, Path.Combine(root, "pos.zar"), "-o", Path.Combine(root, "opt.zar"));
        Assert.Equal(-1, mine.ExitCode);
        Assert.Contains("Too many paths specified", mine.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputOption_SingleInput_Succeeds()
    {
        var root = NewTempDir("o_ok");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");
        var outZar = Path.Combine(root, "out.zar");

        var mine = CliRunner.RunMine(src, "-o", outZar);
        Assert.Equal(0, mine.ExitCode);
        Assert.True(File.Exists(outZar), "Expected archive missing.");
    }

    [Fact]
    public void OutputOption_WithoutInput_FailsUsageWithoutSideEffects()
    {
        // Regression: `zar -o game.zar` used to dispatch the output path as
        // the input, extracting (or packing) it and exiting 0.
        var root = NewTempDir("o_noinput");
        var src = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");
        var outZar = Path.Combine(root, "game.zar");

        var pack = CliRunner.RunMine(src, outZar);
        Assert.Equal(0, pack.ExitCode);

        var mine = CliRunner.RunMine("-o", outZar);
        Assert.Equal(-1, mine.ExitCode);
        Assert.Contains("missing input path", mine.StdErr, StringComparison.Ordinal);
        Assert.True(File.Exists(outZar), "Existing archive must be untouched.");
        Assert.False(Directory.Exists(Path.Combine(root, "game_extracted")),
            "The output path must not have been dispatched as the input.");
    }
}