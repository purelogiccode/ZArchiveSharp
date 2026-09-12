namespace ZArchiveSharp.Tests;

/// <summary>
/// Black-box tests for the <c>--policy</c> scope rule:
/// <c>--policy</c> is a <c>--batch</c> knob; an explicit non-fail
/// policy anywhere else (single pack/extract, the zstd/seekable
/// subcommands) is a <c>-1</c> usage error, never a silent ignore. Unknown
/// values are <c>-1</c> on every path. Vacuous-pass when the CLI binary is
/// missing or cannot start.
/// </summary>
public sealed class PolicyScopeTests
{
    private static void Cleanup(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: temp cleanup must not fail the test.
        }
    }

    private static string WriteGameDir(string work)
    {
        var src = Directory.CreateDirectory(Path.Combine(work, "game")).FullName;
        File.WriteAllText(Path.Combine(src, "a.txt"), "policy scope");
        return src;
    }

    [Fact]
    public void SinglePack_ExplicitNonFailPolicy_Rejected()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polrej");
        try
        {
            var src = WriteGameDir(work);
            foreach (var policy in new[] { "skip", "overwrite", "auto-rename" })
            {
                var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                    [src, Path.Combine(work, $"game_{policy}.zar"), "--policy", policy]);
                if (!started)
                {
                    return;
                }

                Assert.Equal(-1, exit);
                Assert.Contains("only supported with --batch", stderr, StringComparison.Ordinal);
            }

            Assert.False(File.Exists(Path.Combine(work, "game_skip.zar")),
                "Rejected runs must not pack anything.");
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void SinglePack_InvalidPolicy_Rejected()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polbad");
        try
        {
            var src = WriteGameDir(work);
            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                [src, Path.Combine(work, "game.zar"), "--policy", "bogus"]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-1, exit);
            Assert.Contains("invalid --policy", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void SinglePack_MissingPolicyValue_Rejected()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polmissing");
        try
        {
            var src = WriteGameDir(work);
            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                [src, Path.Combine(work, "game.zar"), "--policy"]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-1, exit);
            Assert.Contains("missing value for --policy", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void SinglePack_ExplicitFailPolicy_Packs()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polfail");
        try
        {
            var src = WriteGameDir(work);
            var zar = Path.Combine(work, "game.zar");
            RedumpIsoTests.RunCli(cli, work, src, zar, "--policy", "fail");
            Assert.True(File.Exists(zar), "Explicit --policy fail must pack like the default.");
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void SingleExtract_ExplicitOverwritePolicy_Rejected()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polextract");
        try
        {
            var src = WriteGameDir(work);
            var zar = Path.Combine(work, "game.zar");
            RedumpIsoTests.RunCli(cli, work, src, zar);

            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                [zar, Path.Combine(work, "out"), "--policy", "overwrite"]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-1, exit);
            Assert.Contains("only supported with --batch", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void ZstdSubcommand_ExplicitNonFailPolicy_Rejected()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polzstd");
        try
        {
            var file = Path.Combine(work, "data.bin");
            File.WriteAllBytes(file, "policy scope zstd"u8.ToArray());
            var (started, exit, _, stderr) = RedumpIsoTests.TryRunCli(cli, work,
                ["zstd", "-c", file, Path.Combine(work, "data.zst"), "--policy", "skip"]);
            if (!started)
            {
                return;
            }

            Assert.Equal(-1, exit);
            Assert.Contains("only supported with --batch", stderr, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void Batch_FailPolicy_Collision_ReturnsRefused()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polbatchfail");
        try
        {
            var inDir = Path.Combine(work, "in");
            var src = Directory.CreateDirectory(Path.Combine(inDir, "game")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), "policy scope");
            var outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            File.WriteAllBytes(Path.Combine(outDir, "game.zar"), "occupied"u8.ToArray());

            var (started, exit, _, stderr) =
                RedumpIsoTests.TryRunCli(cli, work, ["--batch", inDir, outDir]);
            if (!started)
            {
                return;
            }

            // The documented batch collision refusal is -11, not the generic
            // aggregate pack failure -13.
            Assert.Equal(-11, exit);
            Assert.Contains("already exists", stderr, StringComparison.Ordinal);
            Assert.Equal("occupied"u8.ToArray(), File.ReadAllBytes(Path.Combine(outDir, "game.zar")));
        }
        finally
        {
            Cleanup(work);
        }
    }

    [Fact]
    public void Batch_OverwritePolicy_ReplacesExisting()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("polbatch");
        try
        {
            var inDir = Path.Combine(work, "in");
            var src = Directory.CreateDirectory(Path.Combine(inDir, "game")).FullName;
            File.WriteAllText(Path.Combine(src, "a.txt"), "policy scope v1");
            var outDir = Path.Combine(work, "out");

            RedumpIsoTests.RunCli(cli, work, "--batch", inDir, outDir);
            var zar = Path.Combine(outDir, "game.zar");
            Assert.True(File.Exists(zar), "First batch run must pack game.zar.");
            var before = File.ReadAllBytes(zar);

            File.WriteAllText(Path.Combine(src, "a.txt"), "policy scope v2, changed content");
            RedumpIsoTests.RunCli(cli, work, "--batch", "--policy", "overwrite", inDir, outDir);
            Assert.NotEqual(before, File.ReadAllBytes(zar));
        }
        finally
        {
            Cleanup(work);
        }
    }
}