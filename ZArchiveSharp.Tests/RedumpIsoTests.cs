using System.Diagnostics;
using System.Text;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Black-box tests for Redump-aware <c>zar --iso</c>:
/// a Redump ISO must pack its game partition (wave-dependent offset),
/// not the leading video partition. Synthetic Redump ISOs are sparse files at
/// the exact <c>XISOSharp.XgdTables.RedumpIsoLength</c> sizes holding one
/// minimal hand-built XISO at the expected <c>XisoOffset</c>; the real CLI
/// binary packs and extracts them end to end. Vacuous pass (early return)
/// when the CLI was not built (clean clone without the XISOSharp sibling),
/// when the host cannot run it, or when the filesystem refuses &gt;4 GiB
/// sparse files (e.g. FAT32) — same convention as the native-toolchain
/// parity tests.
/// </summary>
public sealed class RedumpIsoTests
{
    // XISOSharp XgdTables literals (drift breaks these tests loudly, by design).
    private const long RedumpLenType8 = 0x208E03800L; // RedumpIsoLength[8] (XGD3)
    private const long RedumpLenType5 = 0x1D3390000L; // RedumpIsoLength[5] (wave-dependent XGD2)
    private const long OffsetXgd3 = 0x02080000L; // XisoOffset[3]
    private const long OffsetXgd2 = 0x0FD90000L; // XisoOffset[1]
    private const long OffsetXgd1 = 0x18300000L; // XisoOffset[0]
    private const long PvdOffset = 0x832DL; // GetWave PVD read offset
    private const string WavePvd3 = "2009011416000000"; // WavePvd[3]: wave 3 -> video 5 -> XGD2

    private const int CliTimeoutMs = 120000;

    internal static byte[] Payload()
    {
        return Encoding.Latin1.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox meets the Xbox. ", 40)));
    }

    internal static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), "zarsharp", prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string? FindCli()
    {
        var env = Environment.GetEnvironmentVariable("ZARSHARP_CLI");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CSharp_ZArchiveSharp.sln")))
            {
                string baseDir = AppContext.BaseDirectory;
                string inferred = baseDir.Contains("Release", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
                foreach (var cfg in new[] { inferred, "Release", "Debug" })
                {
                    foreach (var tfm in new[] { "net10.0", "net9.0", "net8.0" })
                    {
                        var bin = Path.Combine(dir.FullName, "ZArchiveSharp.Cli", "bin", cfg, tfm);
                        var apphost = Path.Combine(bin,
                            OperatingSystem.IsWindows() ? "ZArchiveSharp.Cli.exe" : "ZArchiveSharp.Cli");
                        if (File.Exists(apphost))
                        {
                            return apphost;
                        }

                        var dll = Path.Combine(bin, "ZArchiveSharp.Cli.dll");
                        if (File.Exists(dll))
                        {
                            return dll;
                        }
                    }
                }

                return null;
            }
        }

        return null;
    }

    internal static void RunCli(string cli, string work, params string[] args)
    {
        var (started, exit, stdout, stderr) = TryRunCli(cli, work, args);
        if (!started)
        {
            return; // Host cannot run the CLI here: vacuous pass.
        }

        Assert.True(exit == 0,
            $"CLI failed (exit {exit}): {CliCommand(cli, args)}\nstdout: {stdout}\nstderr: {stderr}");
    }

    internal static string CliCommand(string cli, string[] args)
    {
        return (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? $"dotnet \"{cli}\" " : "") + Quote(args);
    }

    internal static string Quote(string[] values)
    {
        return string.Join(" ", values.Select(v => "\"" + v + "\""));
    }

    /// <summary>Runs the CLI without asserting; false when the host cannot start it.</summary>
    internal static (bool Started, int Exit, string Stdout, string Stderr) TryRunCli(string cli, string work,
        string[] args)
    {
        string fileName = cli;
        string arguments = Quote(args);
        if (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "dotnet";
            arguments = $"\"{cli}\" {arguments}";
        }

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = work,
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, -1, "", "");
        }

        if (proc is null)
        {
            return (false, -1, "", "");
        }

        using (proc)
        {
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(CliTimeoutMs))
            {
                try
                {
                    proc.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Already exiting; the exit code below still fails the test.
                }

                Assert.Fail($"CLI timed out: {fileName} {arguments}");
            }

            return (true, proc.ExitCode, stdout, stderr);
        }
    }

    private const string NoXisoMarker = "without XISOSharp support";

    /// <summary>
    /// True when the CLI under test was built with XISOSharp (the iso flag
    /// works). Probes with a tiny non-ISO file: any outcome except the
    /// no-XISO capability error proves the XISO code path is compiled in.
    /// </summary>
    internal static bool CliSupportsIso(string cli)
    {
        string probeDir = NewTempDir("isoprobe");
        try
        {
            string probe;
            try
            {
                probe = Path.Combine(probeDir, "probe.bin");
                File.WriteAllBytes(probe, new byte[] { 1, 2, 3 });
            }
            catch (IOException)
            {
                return true; // Cannot probe; let the real test decide.
            }

            var (started, exit, _, stderr) = TryRunCli(cli, probeDir,
                new[] { "--iso", probe, Path.Combine(probeDir, "probe.zar") });
            if (!started)
            {
                return true; // Host cannot run the CLI; existing guards handle it.
            }

            return exit == 0 || !stderr.Contains(NoXisoMarker, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(probeDir, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp cleanup must not fail the test.
            }
        }
    }

    /// <summary>
    /// Writes a minimal single-file XDVDFS image at <paramref name="baseOffset"/>
    /// inside <paramref name="path"/>: header root pointer, one root-table entry
    /// (left/right 0, plain file), then the file bytes. Only what
    /// <c>XisoZarchive.CreateZar</c> reads (no magic check on that path).
    /// </summary>
    internal static void WriteMinimalXiso(string path, long baseOffset, string fileName, byte[] payload)
    {
        byte[] name = Encoding.Latin1.GetBytes(fileName);
        Assert.True(name.Length is > 0 and <= 255, "Test XISO name must fit in one length byte.");
        const uint rootSector = 0x200;
        const uint fileSector = 0x300;
        using var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.Latin1, leaveOpen: true);
        fs.Seek(baseOffset + 0x10000 + 20, SeekOrigin.Begin);
        bw.Write(rootSector);
        bw.Write(2048u);
        fs.Seek(baseOffset + (rootSector * 2048), SeekOrigin.Begin);
        bw.Write((ushort)0); // left: none (nonzero sector/size below proves non-empty table)
        bw.Write((ushort)0); // right: none
        bw.Write(fileSector);
        bw.Write((uint)payload.Length);
        bw.Write((byte)0); // attrs: file
        bw.Write((byte)name.Length);
        bw.Write(name);
        fs.Seek(baseOffset + (fileSector * 2048), SeekOrigin.Begin);
        bw.Write(payload);
        bw.Flush();
    }

    /// <summary>Creates a sparse file of <paramref name="size"/> bytes, or null when the filesystem refuses.</summary>
    internal static bool TryCreateSparse(string path, long size)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.SetLength(size);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // e.g. FAT32 cannot hold multi-GB images: vacuous pass.
        }
    }

    internal static void AssertExtractsPayload(string cli, string work, string zarPath, string fileName, byte[] payload)
    {
        string outDir = Path.Combine(work, "extracted");
        RunCli(cli, work, zarPath, outDir);
        string[] files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories);
        Assert.Single(files);
        Assert.Equal(fileName, Path.GetFileName(files[0]));
        Assert.Equal(payload, File.ReadAllBytes(files[0]));
    }

    [Fact]
    public void Redump_Type8_PacksGamePartition()
    {
        var cli = FindCli();
        if (cli is null)
        {
            return;
        }

        if (!CliSupportsIso(cli))
        {
            return;
        }

        var work = NewTempDir("redump8");
        try
        {
            string iso = Path.Combine(work, "game.redump.iso");
            if (!TryCreateSparse(iso, RedumpLenType8))
            {
                return;
            }

            byte[] payload = Payload();
            WriteMinimalXiso(iso, OffsetXgd3, "hello.txt", payload);
            string zar = Path.Combine(work, "game.zar");
            RunCli(cli, work, "--iso", iso, zar);
            AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
        }
        finally
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
    }

    [Fact]
    public void Redump_Type5_WavePvd_ResolvesXgd2Offset()
    {
        var cli = FindCli();
        if (cli is null)
        {
            return;
        }

        if (!CliSupportsIso(cli))
        {
            return;
        }

        var work = NewTempDir("redump5");
        try
        {
            string iso = Path.Combine(work, "wave.redump.iso");
            if (!TryCreateSparse(iso, RedumpLenType5))
            {
                return;
            }

            byte[] payload = Payload();
            WriteMinimalXiso(iso, OffsetXgd2, "hello.txt", payload);
            using (var fs = new FileStream(iso, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Seek(PvdOffset, SeekOrigin.Begin);
                byte[] pvd = Encoding.ASCII.GetBytes(WavePvd3);
                fs.Write(pvd, 0, pvd.Length);
            }

            string zar = Path.Combine(work, "wave.zar");
            RunCli(cli, work, "--iso", iso, zar);
            AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
        }
        finally
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
    }

    [Fact]
    public void Redump_Type5_UnknownWave_FallsBackInsteadOfFailing()
    {
        var cli = FindCli();
        if (cli is null)
        {
            return;
        }

        if (!CliSupportsIso(cli))
        {
            return;
        }

        var work = NewTempDir("redump5u");
        try
        {
            string iso = Path.Combine(work, "nowave.redump.iso");
            if (!TryCreateSparse(iso, RedumpLenType5))
            {
                return;
            }

            // Zero PVD: no wave resolves, so the CLI falls back to video type 0
            // (XGD1 offset), exactly like XISOSharp.Cli --zar.
            byte[] payload = Payload();
            WriteMinimalXiso(iso, OffsetXgd1, "hello.txt", payload);
            string zar = Path.Combine(work, "nowave.zar");
            RunCli(cli, work, "--iso", iso, zar);
            AssertExtractsPayload(cli, work, zar, "hello.txt", payload);
        }
        finally
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
    }

    [Fact]
    public void Redump_Pack_ByteIdenticalToPlainIsoPack()
    {
        var cli = FindCli();
        if (cli is null)
        {
            return;
        }

        if (!CliSupportsIso(cli))
        {
            return;
        }

        var work = NewTempDir("redumpid");
        try
        {
            byte[] payload = Payload();
            string plain = Path.Combine(work, "plain.iso");
            WriteMinimalXiso(plain, 0, "hello.txt", payload);
            string redump = Path.Combine(work, "game.redump.iso");
            if (!TryCreateSparse(redump, RedumpLenType8))
            {
                return;
            }

            WriteMinimalXiso(redump, OffsetXgd3, "hello.txt", payload);
            string plainZar = Path.Combine(work, "plain.zar");
            string redumpZar = Path.Combine(work, "game.zar");
            RunCli(cli, work, "--iso", plain, plainZar);
            RunCli(cli, work, "--iso", redump, redumpZar);
            Assert.Equal(File.ReadAllBytes(plainZar), File.ReadAllBytes(redumpZar));
        }
        finally
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
    }
}