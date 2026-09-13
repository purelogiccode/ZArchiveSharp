namespace ZArchiveSharp.CliBattleTests;

/// <summary>
/// Argument- and error-contract battles: both binaries must agree on exit
/// codes; stdout differences that are intentional (extended zar help) or
/// known drifts are locked explicitly so they cannot rot silently.
/// </summary>
public sealed class ArgParityTests : IDisposable
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
    public void NoArgs_BothSucceed_HelpTextDiffersByDesign()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return; // clean clone without References/zarchive.exe: nothing to battle.
        }

        var oracle = CliRunner.RunOracle();
        var mine = CliRunner.RunMine();

        Assert.Equal(0, oracle.ExitCode);
        Assert.Equal(0, mine.ExitCode);
        // The oracle prints the minimal zarchive.exe usage; the CLI intentionally
        // prints its extended help (subcommands, batch flags) under the name it
        // was launched as (the standalone bundle is ZArchiveSharp, not zar).
        Assert.Contains("zarchive.exe input_path", oracle.StdOut, StringComparison.Ordinal);
        var expectedName = Path.GetFileNameWithoutExtension(BinaryLocator.UnderTestExe());
        Assert.Contains($"Usage: {expectedName}", mine.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void TooManyPaths_BothFailUsage_SameMessage(int pathCount)
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        // Shared args list is exactly `input_path [output_path]` on both
        // binaries: any extra positional is a usage error with the
        // oracle-identical message on stdout.
        var args = Enumerable.Range(0, pathCount).Select(i => $"p{i}").ToArray();
        var oracle = CliRunner.RunOracle(args);
        var mine = CliRunner.RunMine(args);

        Assert.Equal(-1, oracle.ExitCode);
        Assert.Equal(-1, mine.ExitCode);
        Assert.Contains("Too many paths specified", oracle.StdOut, StringComparison.Ordinal);
        Assert.Contains("Too many paths specified", mine.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingInput_BothBadUsage()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir("missing");
        var missing = Path.Combine(root, "nope");

        var oracle = CliRunner.RunOracle(missing);
        var mine = CliRunner.RunMine(missing);

        Assert.Equal(-1, oracle.ExitCode);
        Assert.Equal(-1, mine.ExitCode);
        Assert.Contains("not a valid file or directory", oracle.Combined, StringComparison.Ordinal);
        Assert.Contains("not a valid file or directory", mine.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public void PackRefusesExisting_BothRefused()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir("refuse");
        var src = Path.Combine(root, "game");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");

        var oracleOut = Path.Combine(root, "oracle.zar");
        var mineOut = Path.Combine(root, "mine.zar");
        File.WriteAllBytes(oracleOut, [0xAA, 0xBB]);
        File.WriteAllBytes(mineOut, [0xAA, 0xBB]);

        var oracle = CliRunner.RunOracle(src, oracleOut);
        var mine = CliRunner.RunMine(src, mineOut);

        Assert.Equal(-11, oracle.ExitCode);
        Assert.Equal(-11, mine.ExitCode);
        Assert.Contains("already exists", oracle.Combined, StringComparison.Ordinal);
        Assert.Contains("already exists", mine.Combined, StringComparison.Ordinal);
        Assert.Equal([0xAA, 0xBB], File.ReadAllBytes(oracleOut));
        Assert.Equal([0xAA, 0xBB], File.ReadAllBytes(mineOut));
    }

    [Fact]
    public void PackOutputIsDirectory_BothNotFound()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir("packisdir");
        var src = Path.Combine(root, "game");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");
        var clash = Directory.CreateDirectory(Path.Combine(root, "clash")).FullName;

        var oracle = CliRunner.RunOracle(src, clash);
        var mine = CliRunner.RunMine(src, clash);

        Assert.Equal(-10, oracle.ExitCode);
        Assert.Equal(-10, mine.ExitCode);
        Assert.Contains("not a valid file", oracle.Combined, StringComparison.Ordinal);
        Assert.Contains("not a valid file", mine.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractOutputIsFile_BothOutputNotDirectory()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir("extisfile");
        var src = Path.Combine(root, "game");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");

        var zar = Path.Combine(root, "game.zar");
        Assert.Equal(0, CliRunner.RunOracle(src, zar).ExitCode);

        var blocker = Path.Combine(root, "blocker");
        File.WriteAllText(blocker, "x");

        var oracle = CliRunner.RunOracle(zar, blocker);
        var mine = CliRunner.RunMine(zar, blocker);

        Assert.Equal(-3, oracle.ExitCode);
        Assert.Equal(-3, mine.ExitCode);
        Assert.Contains("not a valid directory", oracle.Combined, StringComparison.Ordinal);
        Assert.Contains("not a valid directory", mine.Combined, StringComparison.Ordinal);
    }

    [Fact]
    public void GarbageExtract_BothRefused_KnownPeriodDrift()
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir("garbage");
        var garbage = Path.Combine(root, "garbage.zar");
        File.WriteAllBytes(garbage, [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4]);

        var oracle = CliRunner.RunOracle(garbage, Path.Combine(root, "o_out"));
        var mine = CliRunner.RunMine(garbage, Path.Combine(root, "m_out"));

        Assert.Equal(-11, oracle.ExitCode);
        Assert.Equal(-11, mine.ExitCode);
        // Same sentence, but zar ends it with a period while the oracle does not.
        Assert.Contains("Failed to open ZArchive", oracle.Combined, StringComparison.Ordinal);
        Assert.Contains("Failed to open ZArchive.", mine.Combined, StringComparison.Ordinal);
    }
}