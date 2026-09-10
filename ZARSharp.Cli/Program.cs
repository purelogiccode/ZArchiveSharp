using ZARSharp;
using ZARSharp.Pipeline;
using ZARSharp.Zstd;
using XISOSharp;

namespace ZARSharp.Cli;

/// <summary>ZAR command-line entry point (pack, extract, XISO convert, batch).</summary>
public static class Program
{
    /// <summary>Runs the CLI and returns a <see cref="ZARSharp.Pipeline.ZarchiveCli"/>-compatible exit code.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code (0 on success).</returns>
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string? isoPath = null;
        string? inputPath = null;
        string? outputPath = null;
        int jobs = 4;
        string policy = "fail";
        int level = 6;
        bool quiet = false;
        bool noCompress = false;
        bool batch = false;
        string? dictPath = null;
        bool stdoutFlag = false;
        bool? checksumOverride = null;
        bool helpRequested = false;

        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--iso" or "-i":
                    if (i + 1 < args.Length) isoPath = args[++i];
                    break;
                case "--batch" or "-b":
                    batch = true;
                    break;
                case "--output" or "-o":
                    if (i + 1 < args.Length) outputPath = args[++i];
                    break;
                case "--jobs" or "-j":
                    if (i + 1 < args.Length && int.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out int j)) jobs = j;
                    break;
                case "--policy" or "-p":
                    if (i + 1 < args.Length) policy = args[++i];
                    break;
                case "--level" or "-l":
                    if (i + 1 < args.Length && int.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out int lv)) level = lv;
                    break;
                case "--dict":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: missing value for --dict (expected a dictionary file).");
                        return ZarchiveCli.BadUsage;
                    }

                    dictPath = args[++i];
                    break;
                case "--stdout" or "-c":
                    stdoutFlag = true;
                    break;
                case "--check":
                    checksumOverride = true;
                    break;
                case "--no-check":
                    checksumOverride = false;
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--no-compress":
                    noCompress = true;
                    break;
                case "--help" or "-h":
                    helpRequested = true;
                    break;
                case "--version" or "-v":
                    Console.WriteLine($"zar {GetVersion()}");
                    return 0;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count > 0 && inputPath == null) inputPath = positional[0];
        if (positional.Count > 1 && outputPath == null) outputPath = positional[1];

        // New subcommand (additive; the positional pack/extract path below is
        // untouched). To pack/extract a path literally named "zstd", spell it
        // ./zstd so it is not taken for the subcommand.
        if (string.Equals(positional.FirstOrDefault(), "zstd", StringComparison.Ordinal))
        {
            var sub = positional.Skip(1).ToList();
            if (helpRequested)
            {
                sub.Add("--help");
            }

            return RunZstd(sub.ToArray(), level, dictPath, checksumOverride ?? false, quiet, stdoutFlag);
        }

        if (helpRequested)
        {
            PrintUsage();
            return 0;
        }

        if (stdoutFlag)
        {
            Console.Error.WriteLine("Error: --stdout is only supported with 'zar zstd'.");
            return ZarchiveCli.BadUsage;
        }

        ZstdDictionary? dictionary = null;
        if (dictPath != null)
        {
            dictionary = TryLoadDictionary(dictPath);
            if (dictionary == null)
            {
                return ZarchiveCli.BadUsage;
            }
        }

        bool checksum = checksumOverride ?? false;

        var collisionPolicy = policy.ToLowerInvariant() switch
        {
            "skip" => ZarCollisionPolicy.Skip,
            "overwrite" => ZarCollisionPolicy.Overwrite,
            "auto-rename" or "autorename" => ZarCollisionPolicy.AutoRename,
            _ => ZarCollisionPolicy.Fail,
        };

        IZarBlockCompressor? compressor = noCompress ? new ZarRawCompressor() : null;

        // Mode 1: XISO → .zar
        if (isoPath != null)
        {
            return PackIso(isoPath, outputPath, level, quiet, compressor, dictionary);
        }

        // Mode 2: Batch operations
        if (batch)
        {
            return RunBatch(inputPath, outputPath, jobs, collisionPolicy, level, quiet, compressor, dictionary, checksum);
        }

        // Mode 3: Standard pack/extract (zarchive.exe compat)
        var options = new ZarPipelineOptions
        {
            Level = level,
            Checksum = checksum,
            Dictionary = dictionary,
            CollisionPolicy = collisionPolicy,
            Compressor = compressor,
            MaxDegreeOfParallelism = jobs,
        };

        var args2 = new List<string>();
        if (inputPath != null) args2.Add(inputPath);
        if (outputPath != null) args2.Add(outputPath);

        return ZarchiveCli.Run(args2.ToArray(), options, log: quiet ? null : Console.WriteLine);
    }

    private static int RunZstd(
        string[] zstdArgs, int level, string? dictPath, bool checksum, bool quiet, bool stdoutFlag)
    {
        if (!ZstdCli.TryParse(zstdArgs, out var job, out string? parseError,
            defaultLevel: level, defaultDictPath: dictPath, defaultChecksum: checksum,
            defaultQuiet: quiet, defaultStdout: stdoutFlag))
        {
            Console.Error.WriteLine($"Error: {parseError}");
            Console.Error.WriteLine(ZstdCli.UsageText);
            return ZarchiveCli.BadUsage;
        }

        // Binary output to stdout: informational lines must go to stderr or
        // they would corrupt piped data. Help text is never binary, so like
        // the global --help it goes to stdout.
        Action<string>? log = job!.Quiet && !job.ShowHelp
            ? null
            : job.ShowHelp || job.OutputPath is not null ? Console.WriteLine : Console.Error.WriteLine;

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            return ZstdCli.RunAsync(job, Console.OpenStandardInput(), Console.OpenStandardOutput(),
                log, Console.Error.WriteLine, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Canceled.");
            return 130; // SIGINT shell convention, not a pack/extract code.
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static ZstdDictionary? TryLoadDictionary(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: dictionary file not found: {path}");
            return null;
        }

        try
        {
            return ZstdDictionary.FromBytes(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or ZstdException)
        {
            Console.Error.WriteLine($"Error: invalid dictionary file '{path}': {ex.Message}");
            return null;
        }
    }

    private static int PackIso(string isoPath, string? zarPath, int level, bool quiet, IZarBlockCompressor? compressor, ZstdDictionary? dictionary)
    {
        if (!File.Exists(isoPath))
        {
            Console.Error.WriteLine($"Error: ISO file not found: {isoPath}");
            return -10;
        }

        string output = zarPath ?? DeriveZarPath(isoPath);

        if (File.Exists(output))
        {
            Console.Error.WriteLine($"Error: Output file already exists: {output}");
            return -11;
        }

        if (!quiet) Console.WriteLine($"Converting XISO to ZAR: {isoPath} -> {output}");

        IZarBlockCompressor? comp = compressor;
        if (comp == null && (level != 6 || dictionary != null))
        {
            comp = new ZstdCompressor(new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
        }

        var progress = quiet ? null : new Progress<ZarProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                double pct = (double)p.BytesCompleted / p.BytesTotal * 100;
                Console.Write($"\r  {pct:F1}% ({p.BytesCompleted / (1024 * 1024)} / {p.BytesTotal / (1024 * 1024)} MiB)");
            }
        });

        try
        {
            bool ok = XisoZarchive.CreateZar(isoPath, output, quiet: quiet, compressor: comp, progress: progress);
            if (!quiet) Console.WriteLine();
            if (ok)
            {
                if (!quiet) Console.WriteLine($"Done: {output}");
                return 0;
            }

            Console.Error.WriteLine("Error: XISO conversion failed.");
            return -13;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return -13;
        }
    }

    private static int RunBatch(string? inputPath, string? outputPath, int jobs, ZarCollisionPolicy policy,
        int level, bool quiet, IZarBlockCompressor? compressor, ZstdDictionary? dictionary, bool checksum)
    {
        if (inputPath == null || !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine("Error: --batch requires an input directory.");
            return -1;
        }

        string destDir = outputPath ?? inputPath;

        var files = ProcessableFiles.Find(inputPath, ZarProcessMode.Auto);
        if (files.Count == 0)
        {
            if (!quiet) Console.WriteLine("No processable files found.");
            return 0;
        }

        if (!quiet) Console.WriteLine($"Found {files.Count} file(s). Processing with {jobs} workers...");

        var options = new ZarPipelineOptions
        {
            Level = level,
            Checksum = checksum,
            Dictionary = dictionary,
            CollisionPolicy = policy,
            Compressor = compressor,
            MaxDegreeOfParallelism = jobs,
        };

        var progress = quiet ? null : new Progress<ZarProgress>(p =>
        {
            if (p.Operation == ZarOperation.Pack)
            {
                double pct = p.Ratio * 100;
                Console.Write($"\r  [{p.FilesCompleted}/{p.FilesTotal}] {pct:F1}% {Path.GetFileName(p.SourcePath)}");
            }
        });

        try
        {
            var results = ZarPipeline.PackBatch(files, destDir, options, progress);

            if (!quiet) Console.WriteLine();

            int ok = 0, fail = 0;
            foreach (var r in results)
            {
                if (r.Status == ZarItemStatus.Completed)
                    ok++;
                else
                {
                    fail++;
                    Console.Error.WriteLine($"  Failed: {r.SourcePath} - {r.ErrorMessage}");
                }
            }

            if (!quiet) Console.WriteLine($"Batch complete: {ok} succeeded, {fail} failed.");
            return fail > 0 ? -13 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return -13;
        }
    }

    private static string GetVersion()
    {
        var info = typeof(Program).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        if (string.IsNullOrEmpty(info))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";
        }

        // MinVer stamps e.g. "0.0.0-alpha.0.4+githash"; keep the version core.
        int plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    private static string DeriveZarPath(string isoPath)
    {
        string dir = Path.GetDirectoryName(isoPath) ?? "";
        string name = Path.GetFileNameWithoutExtension(isoPath);
        if (name.EndsWith(".redump", StringComparison.OrdinalIgnoreCase))
            name = name[..^".redump".Length];
        return Path.Combine(dir, $"{name}.zar");
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: zar [options] [input] [output]");
        Console.WriteLine("       zar zstd -c|-d [options] [input] [output]");
        Console.WriteLine();
        Console.WriteLine("Pack a directory to .zar, extract a .zar, convert an XISO, or");
        Console.WriteLine("compress/decompress single zstd streams:");
        Console.WriteLine("  zar <directory> [output.zar]         Pack directory to .zar");
        Console.WriteLine("  zar <archive.zar> [output_dir]       Extract .zar to directory");
        Console.WriteLine("  zar --iso <game.iso> [output.zar]    Convert XISO to .zar");
        Console.WriteLine("  zar zstd -c [in] [out]               Compress a file/stdin to zstd");
        Console.WriteLine("  zar zstd -d [in] [out]               Decompress a zstd file/stdin");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -l, --level <N>       Compression level 1-22 (default: 6)");
        Console.WriteLine("      --dict <file>     Dictionary file (pack/zstd; kept alongside,");
        Console.WriteLine("                        never stored; --no-compress ignores it)");
        Console.WriteLine("  -c, --stdout          Stream to stdout (only with 'zar zstd'; inside");
        Console.WriteLine("                        'zar zstd', -c means --compress instead)");
        Console.WriteLine("      --check           Write content checksums (pack/zstd)");
        Console.WriteLine("      --no-check        Do not write checksums (default; last wins)");
        Console.WriteLine("  -j, --jobs <N>        Parallel workers (default: 4)");
        Console.WriteLine("  -p, --policy <P>      Collision policy: fail, skip, overwrite, auto-rename");
        Console.WriteLine("  -b, --batch           Batch process all files in input directory");
        Console.WriteLine("  -o, --output <path>   Output path");
        Console.WriteLine("  -q, --quiet           Suppress output");
        Console.WriteLine("      --no-compress     Store blocks without compression");
        Console.WriteLine("  -v, --version         Show version");
        Console.WriteLine("  -h, --help            Show this help");
        Console.WriteLine();
        Console.WriteLine("Run 'zar zstd --help' for the zstd subcommand. A path literally");
        Console.WriteLine("named 'zstd' must be spelled ./zstd to pack/extract it.");
    }
}
