using System.Security.Cryptography;
using Xunit.Abstractions;

namespace ZArchiveSharp.CliBattleTests;

/// <summary>
/// Pack/extract battles over every fixture: both binaries must exit 0, and
/// every archive must extract to the original tree no matter which binary
/// packed it and which one extracts it (cross-interop both directions).
/// Archive bytes are NOT required to be identical (zstd encoder drift is
/// legitimate); hashes are logged for manual inspection instead.
/// </summary>
public sealed class PackExtractParityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _tempDirs = [];

    public PackExtractParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

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

    public static TheoryData<string> FixtureNames => BattleFixtures.Names;

    private string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zar_battle",
            prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void PackExplicit_RoundTripAndCrossInterop(string fixtureName)
    {
        var root = NewTempDir($"pack_{fixtureName}");
        var (oracleSrc, mineSrc) = BattleFixtures.CreateTwinCopies(root, fixtureName);
        var oracleZar = Path.Combine(root, "oracle.zar");
        var mineZar = Path.Combine(root, "mine.zar");

        var packOracle = CliRunner.RunOracle(oracleSrc, oracleZar);
        var packMine = CliRunner.RunMine(mineSrc, mineZar);
        Assert.Equal(0, packOracle.ExitCode);
        Assert.Equal(0, packMine.ExitCode);
        Assert.True(new FileInfo(oracleZar).Length > 0, "Oracle produced an empty archive.");
        Assert.True(new FileInfo(mineZar).Length > 0, "zar produced an empty archive.");
        LogHashes(fixtureName, oracleZar, mineZar);

        // Self round-trips. File-less archives (empty_dir) are not openable
        // by either binary: both refuse with -11, so parity means failing
        // identically rather than round-tripping.
        var oracleSelf = Path.Combine(root, "oracle_self");
        var mineSelf = Path.Combine(root, "mine_self");
        if (IsFilelessFixture(fixtureName))
        {
            AssertExtractRefused(CliRunner.RunOracle(oracleZar, oracleSelf));
            AssertExtractRefused(CliRunner.RunMine(mineZar, mineSelf));
            return;
        }

        Assert.Equal(0, CliRunner.RunOracle(oracleZar, oracleSelf).ExitCode);
        Assert.Equal(0, CliRunner.RunMine(mineZar, mineSelf).ExitCode);
        TreeAssert.EqualDirectories(oracleSrc, oracleSelf);
        TreeAssert.EqualDirectories(mineSrc, mineSelf);

        // Cross-interop both directions: the actual compatibility contract.
        var oracleByMine = Path.Combine(root, "oracle_by_mine");
        var mineByOracle = Path.Combine(root, "mine_by_oracle");
        var crossMine = CliRunner.RunMine(oracleZar, oracleByMine);
        var crossOracle = CliRunner.RunOracle(mineZar, mineByOracle);
        Assert.Equal(0, crossMine.ExitCode);
        Assert.Equal(0, crossOracle.ExitCode);
        TreeAssert.EqualDirectories(oracleSrc, oracleByMine);
        TreeAssert.EqualDirectories(mineSrc, mineByOracle);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void PackDefaultOutput_BothSucceed(string fixtureName)
    {
        var root = NewTempDir($"defpack_{fixtureName}");
        var (oracleSrc, mineSrc) = BattleFixtures.CreateTwinCopies(root, fixtureName);

        var packOracle = CliRunner.RunOracle(oracleSrc);
        var packMine = CliRunner.RunMine(mineSrc);

        Assert.Equal(0, packOracle.ExitCode);
        Assert.Equal(0, packMine.ExitCode);
        // Default is <parent>/<stem>.zar next to the input; only the slash
        // direction in the chatter differs (generic_string vs OS path).
        var expectedOracleZar = Path.Combine(root, "src_oracle.zar");
        var expectedMineZar = Path.Combine(root, "src_mine.zar");
        Assert.True(File.Exists(expectedOracleZar), $"Oracle default output missing: {expectedOracleZar}");
        Assert.True(File.Exists(expectedMineZar), $"zar default output missing: {expectedMineZar}");
        Assert.Contains("Outputting to:", ZarLog.SlashToForward(packOracle.StdOut), StringComparison.Ordinal);
        Assert.Contains("Outputting to:", ZarLog.SlashToForward(packMine.StdOut), StringComparison.Ordinal);

        var oracleOut = Path.Combine(root, "oracle_out");
        var mineOut = Path.Combine(root, "mine_out");
        if (IsFilelessFixture(fixtureName))
        {
            AssertExtractRefused(CliRunner.RunMine(expectedOracleZar, oracleOut));
            AssertExtractRefused(CliRunner.RunOracle(expectedMineZar, mineOut));
            return;
        }

        Assert.Equal(0, CliRunner.RunMine(expectedOracleZar, oracleOut).ExitCode);
        Assert.Equal(0, CliRunner.RunOracle(expectedMineZar, mineOut).ExitCode);
        TreeAssert.EqualDirectories(oracleSrc, oracleOut);
        TreeAssert.EqualDirectories(mineSrc, mineOut);
    }

    [Fact]
    public void ExtractDefaultOutput_BothSucceed()
    {
        var root = NewTempDir("defext");
        var src = Path.Combine(root, "game");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello\n");

        var oracleZar = Path.Combine(root, "oracle.zar");
        Assert.Equal(0, CliRunner.RunOracle(src, oracleZar).ExitCode);
        var oracleCopy = Path.Combine(root, "copy_o.zar");
        var mineCopy = Path.Combine(root, "copy_m.zar");
        File.Copy(oracleZar, oracleCopy);
        File.Copy(oracleZar, mineCopy);

        // Default extract dir is <stem>_extracted next to the archive, and
        // both binaries announce it (slash direction aside).
        var extractOracle = CliRunner.RunOracle(oracleCopy);
        var extractMine = CliRunner.RunMine(mineCopy);
        Assert.Equal(0, extractOracle.ExitCode);
        Assert.Equal(0, extractMine.ExitCode);
        Assert.Contains("Extracting to:", ZarLog.SlashToForward(extractOracle.StdOut), StringComparison.Ordinal);
        Assert.Contains("Extracting to:", ZarLog.SlashToForward(extractMine.StdOut), StringComparison.Ordinal);

        TreeAssert.EqualDirectories(src, Path.Combine(root, "copy_o_extracted"));
        TreeAssert.EqualDirectories(src, Path.Combine(root, "copy_m_extracted"));
    }

    [Fact]
    public void PackChatter_AddingLinesMatch()
    {
        var root = NewTempDir("packchat");
        var (oracleSrc, mineSrc) = BattleFixtures.CreateTwinCopies(root, "nested");

        var packOracle = CliRunner.RunOracle(oracleSrc, Path.Combine(root, "o.zar"));
        var packMine = CliRunner.RunMine(mineSrc, Path.Combine(root, "m.zar"));
        Assert.Equal(0, packOracle.ExitCode);
        Assert.Equal(0, packMine.ExitCode);

        List<string> AddingLines(CliResult r) => r.StdOutLines()
            .Where(l => l.StartsWith("Adding ", StringComparison.Ordinal))
            .Select(ZarLog.SlashToForward)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(AddingLines(packOracle), AddingLines(packMine));
    }

    [Fact]
    public void ExtractChatter_NestedLeadingSlashDrift()
    {
        var root = NewTempDir("extchat");
        var src = Path.Combine(root, "game");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hi\n");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "mid\n");
        File.WriteAllText(Path.Combine(src, "sub", "deep", "c.txt"), "deep\n");

        var zar = Path.Combine(root, "game.zar");
        Assert.Equal(0, CliRunner.RunOracle(src, zar).ExitCode);

        var oracleZar = Path.Combine(root, "o.zar");
        var mineZar = Path.Combine(root, "m.zar");
        File.Copy(zar, oracleZar);
        File.Copy(zar, mineZar);

        var extractOracle = CliRunner.RunOracle(oracleZar, Path.Combine(root, "o_out"));
        var extractMine = CliRunner.RunMine(mineZar, Path.Combine(root, "m_out"));
        Assert.Equal(0, extractOracle.ExitCode);
        Assert.Equal(0, extractMine.ExitCode);

        List<string> EntryLines(CliResult r) => r.StdOutLines()
            .Where(l => !l.StartsWith("Extracting to:", StringComparison.Ordinal))
            .Select(ZarLog.SlashToForward)
            .ToList();

        var oracleLines = EntryLines(extractOracle);
        var mineLines = EntryLines(extractMine);

        // Top-level entries are identical; nested files drift: the oracle
        // always prefixes '/', zar omits it below the top level.
        Assert.Contains("/a.txt", oracleLines);
        Assert.Contains("/a.txt", mineLines);
        Assert.Contains("/sub", oracleLines);
        Assert.Contains("/sub", mineLines);
        Assert.Contains("/sub/b.txt", oracleLines);
        Assert.Contains("sub/b.txt", mineLines);
        Assert.Contains("/sub/deep/c.txt", oracleLines);
        Assert.Contains("sub/deep/c.txt", mineLines);
    }

    private static bool IsFilelessFixture(string fixtureName) =>
        string.Equals(fixtureName, "empty_dir", StringComparison.Ordinal);

    private static void AssertExtractRefused(CliResult result)
    {
        Assert.Equal(-11, result.ExitCode);
        Assert.Contains("Failed to open ZArchive", result.Combined, StringComparison.Ordinal);
    }

    private void LogHashes(string fixtureName, string oracleZar, string mineZar)
    {
        var oBytes = File.ReadAllBytes(oracleZar);
        var mBytes = File.ReadAllBytes(mineZar);
        _output.WriteLine(
            $"[{fixtureName}] oracle={oBytes.Length} bytes sha256={Convert.ToHexString(SHA256.HashData(oBytes))} " +
            $"mine={mBytes.Length} bytes sha256={Convert.ToHexString(SHA256.HashData(mBytes))} " +
            $"byte_identical={oBytes.SequenceEqual(mBytes)}");
    }
}
