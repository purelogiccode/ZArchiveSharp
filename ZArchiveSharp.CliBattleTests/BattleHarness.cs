using System.Diagnostics;

namespace ZArchiveSharp.CliBattleTests;

/// <summary>Captured result of one CLI process invocation.</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => StdOut + StdErr;

    public List<string> StdOutLines() => SplitLines(StdOut);

    public static List<string> SplitLines(string text) =>
        text.Split(["\r\n", "\n"], StringSplitOptions.None)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length != 0)
            .ToList();
}

/// <summary>
/// Locates the two battle binaries: the reference <c>zarchive.exe</c> oracle
/// and the <c>zar</c> CLI under test. Both accept an environment override so
/// CI can point at published artifacts instead of build outputs.
/// </summary>
public static class BinaryLocator
{
    public static string OracleExe()
    {
        var env = Environment.GetEnvironmentVariable("ZAR_ORACLE_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var root = FindRepoRoot();
        var candidate = Path.Combine(root, "References", "zarchive.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            $"Oracle binary not found at '{candidate}'. " +
            "Set ZAR_ORACLE_EXE to the reference zarchive.exe path.");
    }

    public static string UnderTestExe()
    {
        var env = Environment.GetEnvironmentVariable("ZAR_UNDER_TEST_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return env;
        }

        var root = FindRepoRoot();
        // Prefer the newest TFM/config the repo actually built.
        string[] configs = ["Release", "Debug"];
        string[] tfms = ["net10.0", "net9.0", "net8.0"];
        foreach (var config in configs)
        {
            foreach (var tfm in tfms)
            {
                var candidate = Path.Combine(
                    root, "ZArchiveSharp.Cli", "bin", config, tfm,
                    OperatingSystem.IsWindows() ? "ZArchiveSharp.exe" : "ZArchiveSharp");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException(
            "CLI under test not found. Build it first " +
            "(`dotnet build ZArchiveSharp.Cli`) or set ZAR_UNDER_TEST_EXE.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 12 && dir != null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CSharp_ZArchiveSharp.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repo root (CSharp_ZArchiveSharp.sln) " +
            $"walking up from '{AppContext.BaseDirectory}'.");
    }
}

/// <summary>Runs a CLI exe headlessly and captures exit code plus both streams.</summary>
public static class CliRunner
{
    private const int TimeoutMilliseconds = 120_000;

    public static CliResult RunOracle(params string[] args) =>
        Run(BinaryLocator.OracleExe(), args);

    public static CliResult RunMine(params string[] args) =>
        Run(BinaryLocator.UnderTestExe(), args);

    public static CliResult Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = string.Join(" ", args.Select(Escape)),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();
        var outTask = process.StandardOutput.ReadToEndAsync();
        var errTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeoutMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored: the process may have exited between the check and the kill.
            }

            throw new TimeoutException(
                $"Timed out after {TimeoutMilliseconds}ms: {exe} {psi.Arguments}");
        }

        return new CliResult(process.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string Escape(string arg)
    {
        if (arg.Length == 0)
        {
            return "\"\"";
        }

        return arg.Contains('"', StringComparison.Ordinal)
            ? "\"" + arg.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : arg.Any(c => c is ' ' or '\t' or '\n' or '\r')
                ? "\"" + arg + "\""
                : arg;
    }
}
