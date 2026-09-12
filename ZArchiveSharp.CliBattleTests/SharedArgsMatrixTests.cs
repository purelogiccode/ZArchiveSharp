namespace ZArchiveSharp.CliBattleTests;

/// <summary>
/// Exhaustive battle over the shared command args list
/// (<c>zarchive.exe input_path [output_path]</c>, ZArchive 0.1.2
/// <c>src/main.cpp</c>): every input-kind x output-kind combination runs
/// with identical relative args on both binaries. Exit codes and normalized
/// stdout must be identical; produced artifacts must agree mutually and
/// with the source tree. Both sides work in twin directories so identical
/// relative args stay comparable after root normalization.
/// </summary>
public sealed class SharedArgsMatrixTests : IDisposable
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

    public static TheoryData<string> Scenarios
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in new[]
                     {
                         "missing_no_output",
                         "missing_with_output",
                         "dir_no_output",
                         "dir_new_file",
                         "dir_existing_file",
                         "dir_existing_dir",
                         "zar_no_output",
                         "zar_new_dir",
                         "zar_existing_file",
                         "zar_existing_dir",
                         "garbage_new_dir",
                         "empty_dir_new_file",
                     })
            {
                data.Add(name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void SharedArgs_BothAgree(string scenario)
    {
        if (!BinaryLocator.OracleAvailable())
        {
            return;
        }

        var root = NewTempDir($"matrix_{scenario}");
        var oracleHome = Path.Combine(root, "oracle");
        var mineHome = Path.Combine(root, "mine");
        Directory.CreateDirectory(oracleHome);
        Directory.CreateDirectory(mineHome);

        var oracleArgs = Setup(scenario, oracleHome, CliRunner.RunOracle);
        var mineArgs = Setup(scenario, mineHome, CliRunner.RunMine);

        var oracle = CliRunner.RunOracle(oracleArgs);
        var mine = CliRunner.RunMine(mineArgs);

        Assert.Equal(ExpectedExit(scenario), oracle.ExitCode);
        Assert.Equal(ExpectedExit(scenario), mine.ExitCode);
        Assert.Equal(Normalize(oracle.StdOut, oracleHome), Normalize(mine.StdOut, mineHome));
        Assert.Equal(Normalize(oracle.StdErr, oracleHome), Normalize(mine.StdErr, mineHome));
        Verify(scenario, oracleHome, mineHome);
    }

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zar_battle",
            prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static int ExpectedExit(string scenario)
    {
        return scenario switch
        {
            "missing_no_output" or "missing_with_output" => -1,
            "dir_no_output" or "dir_new_file" => 0,
            "dir_existing_file" => -11,
            "dir_existing_dir" => -10,
            "zar_no_output" or "zar_new_dir" or "zar_existing_dir" => 0,
            "zar_existing_file" => -3,
            "garbage_new_dir" => -11,
            "empty_dir_new_file" => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
    }

    /// <summary>Builds one side's twin state and returns its CLI args.</summary>
    private static string[] Setup(string scenario, string home, Func<string[], CliResult> run)
    {
        var src = Path.Combine(home, "src");
        switch (scenario)
        {
            case "missing_no_output":
                return [Path.Combine(home, "nope")];
            case "missing_with_output":
                return [Path.Combine(home, "nope"), Path.Combine(home, "out.zar")];
            case "dir_no_output":
                FlatFixture(src);
                return [src];
            case "dir_new_file":
                FlatFixture(src);
                return [src, Path.Combine(home, "out.zar")];
            case "dir_existing_file":
                FlatFixture(src);
                var packBlocker = Path.Combine(home, "out.zar");
                File.WriteAllBytes(packBlocker, [0xAA]);
                return [src, packBlocker];
            case "dir_existing_dir":
                FlatFixture(src);
                return [src, Directory.CreateDirectory(Path.Combine(home, "clash")).FullName];
            case "zar_no_output":
            case "zar_new_dir":
            case "zar_existing_file":
            case "zar_existing_dir":
            {
                FlatFixture(src);
                var zar = Path.Combine(home, "pre.zar");
                Assert.Equal(0, run([src, zar]).ExitCode);
                return scenario switch
                {
                    "zar_no_output" => [zar],
                    "zar_new_dir" => [zar, Path.Combine(home, "out")],
                    "zar_existing_file" => [zar, CreateBlocker(home)],
                    "zar_existing_dir" => [zar, CreateMergeDir(home)],
                    _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
                };
            }

            case "garbage_new_dir":
            {
                var garbage = Path.Combine(home, "garbage.zar");
                File.WriteAllBytes(garbage, [0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4]);
                return [garbage, Path.Combine(home, "out")];
            }

            case "empty_dir_new_file":
            {
                Directory.CreateDirectory(Path.Combine(src, "empty_sub"));
                return [src, Path.Combine(home, "empty.zar")];
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
    }

    /// <summary>Mutual + source artifact agreement per scenario.</summary>
    private static void Verify(string scenario, string oracleHome, string mineHome)
    {
        switch (scenario)
        {
            case "dir_no_output":
                AssertNonEmptyZar(Path.Combine(oracleHome, "src.zar"));
                AssertNonEmptyZar(Path.Combine(mineHome, "src.zar"));
                break;
            case "dir_new_file":
                AssertNonEmptyZar(Path.Combine(oracleHome, "out.zar"));
                AssertNonEmptyZar(Path.Combine(mineHome, "out.zar"));
                break;
            case "dir_existing_file":
                // Refused packs must leave the blocker untouched.
                Assert.Equal([0xAA], File.ReadAllBytes(Path.Combine(oracleHome, "out.zar")));
                Assert.Equal([0xAA], File.ReadAllBytes(Path.Combine(mineHome, "out.zar")));
                break;
            case "zar_no_output":
                TreeAssert.EqualDirectories(
                    Path.Combine(oracleHome, "src"), Path.Combine(oracleHome, "pre_extracted"));
                TreeAssert.EqualDirectories(
                    Path.Combine(mineHome, "src"), Path.Combine(mineHome, "pre_extracted"));
                TreeAssert.EqualDirectories(
                    Path.Combine(oracleHome, "pre_extracted"), Path.Combine(mineHome, "pre_extracted"));
                break;
            case "zar_new_dir":
                TreeAssert.EqualDirectories(
                    Path.Combine(oracleHome, "src"), Path.Combine(oracleHome, "out"));
                TreeAssert.EqualDirectories(
                    Path.Combine(mineHome, "src"), Path.Combine(mineHome, "out"));
                break;
            case "zar_existing_dir":
                foreach (var home in new[] { oracleHome, mineHome })
                {
                    var dir = Path.Combine(home, "merge");
                    Assert.Equal("keep me\n", File.ReadAllText(Path.Combine(dir, "kept.txt")));
                    Assert.Equal("hello\n", File.ReadAllText(Path.Combine(dir, "a.txt")));
                    Assert.Equal("world\n", File.ReadAllText(Path.Combine(dir, "b.txt")));
                }

                TreeAssert.EqualDirectories(
                    Path.Combine(oracleHome, "merge"), Path.Combine(mineHome, "merge"));
                break;
            case "empty_dir_new_file":
                AssertNonEmptyZar(Path.Combine(oracleHome, "empty.zar"));
                AssertNonEmptyZar(Path.Combine(mineHome, "empty.zar"));
                break;
        }
    }

    private static void FlatFixture(string src)
    {
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");
        File.WriteAllText(Path.Combine(src, "b.txt"), "world\n");
    }

    private static string CreateBlocker(string home)
    {
        var blocker = Path.Combine(home, "blocker");
        File.WriteAllText(blocker, "x");
        return blocker;
    }

    private static string CreateMergeDir(string home)
    {
        // Pre-existing output dir with content: extraction must merge into
        // it, keeping what was there and adding the archived files.
        var dir = Directory.CreateDirectory(Path.Combine(home, "merge")).FullName;
        File.WriteAllText(Path.Combine(dir, "kept.txt"), "keep me\n");
        return dir;
    }

    private static void AssertNonEmptyZar(string path)
    {
        Assert.True(File.Exists(path), $"Expected archive missing: {path}");
        Assert.True(new FileInfo(path).Length > 0, $"Expected non-empty archive: {path}");
    }

    /// <summary>
    /// Makes stdout comparable across twin homes: home prefix to a
    /// placeholder, separators to <c>/</c>, the one locked punctuation
    /// drift normalized (raw texts pinned by
    /// <c>ArgParityTests.GarbageExtract_BothRefused_KnownPeriodDrift</c>),
    /// and <c>Adding</c> lines sorted (directory iteration order is
    /// filesystem-dependent; the contract is the packed set).
    /// </summary>
    private static string Normalize(string text, string home)
    {
        // The oracle prints generic_string paths (forward slashes) while zar
        // prints OS paths: normalize the home prefix in both slash flavors
        // before unifying separators.
        var homeFwd = home.Replace('\\', '/');
        var lines = CliResult.SplitLines(
                text.Replace(home, "<T>", StringComparison.Ordinal)
                    .Replace(homeFwd, "<T>", StringComparison.Ordinal))
            .Select(ZarLog.SlashToForward)
            .Select(l => l == "Failed to open ZArchive." ? "Failed to open ZArchive" : l)
            .ToList();
        var adding = lines
            .Where(l => l.StartsWith("Adding ", StringComparison.Ordinal))
            .OrderBy(l => l, StringComparer.Ordinal);
        var rest = lines.Where(l => !l.StartsWith("Adding ", StringComparison.Ordinal));
        return string.Join("\n", rest.Concat(adding));
    }
}