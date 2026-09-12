namespace ZArchiveSharp.CliBattleTests;

/// <summary>
/// Regression tests for the zar-only <c>zstd</c> subcommand argument handling.
/// The global parser must not steal the subcommand's <c>-c</c> (which means
/// <c>--compress</c> there, not <c>--stdout</c>).
/// </summary>
public sealed class ZstdSubcommandTests : IDisposable
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
    public void Zstd_DashC_CompressesToOutputFile()
    {
        // Regression: the global --stdout handler consumed `-c` and the
        // subcommand failed with "One of -c/--compress or -d/--decompress is
        // required.".
        var root = NewTempDir("zstd_c");
        var input = Path.Combine(root, "data.bin");
        var payload = new byte[40000];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31);
        }

        File.WriteAllBytes(input, payload);
        var compressed = Path.Combine(root, "data.zst");
        var restored = Path.Combine(root, "back.bin");

        var compress = CliRunner.RunMine("zstd", "-c", input, compressed);
        Assert.Equal(0, compress.ExitCode);
        Assert.True(File.Exists(compressed), compress.Combined);

        var decompress = CliRunner.RunMine("zstd", "-d", compressed, restored);
        Assert.Equal(0, decompress.ExitCode);
        Assert.Equal(payload, File.ReadAllBytes(restored));
    }

    [Fact]
    public void Zstd_DashC_ConflictingWithDashD_FailsUsage()
    {
        // The forwarded -c must reach the subcommand parser, which owns the
        // mode-conflict error (the global parser must not swallow it).
        var root = NewTempDir("zstd_cd");
        var input = Path.Combine(root, "data.bin");
        File.WriteAllBytes(input, [1, 2, 3, 4]);
        var output = Path.Combine(root, "data.zst");

        var result = CliRunner.RunMine("zstd", "-c", "-d", input, output);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Cannot combine", result.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }
}