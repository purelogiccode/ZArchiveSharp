using ZARSharp;
using ZARSharp.Pipeline;
using ZARSharp.Zstd;
using XISOSharp;

namespace ZARSharp.Cli;

public static class Program
{
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
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--no-compress":
                    noCompress = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                case "--version" or "-v":
                    Console.WriteLine("zar 1.0.0");
                    return 0;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count > 0 && inputPath == null) inputPath = positional[0];
        if (positional.Count > 1 && outputPath == null) outputPath = positional[1];

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
            return PackIso(isoPath, outputPath, level, quiet, compressor);
        }

        // Mode 2: Batch operations
        if (batch)
        {
            return RunBatch(inputPath, outputPath, jobs, collisionPolicy, level, quiet, compressor);
        }

        // Mode 3: Standard pack/extract (zarchive.exe compat)
        var options = new ZarPipelineOptions
        {
            Level = level,
            CollisionPolicy = collisionPolicy,
            Compressor = compressor,
            MaxDegreeOfParallelism = jobs,
        };

        var args2 = new List<string>();
        if (inputPath != null) args2.Add(inputPath);
        if (outputPath != null) args2.Add(outputPath);

        return ZarchiveCli.Run(args2.ToArray(), options, log: quiet ? null : Console.WriteLine);
    }

    private static int PackIso(string isoPath, string? zarPath, int level, bool quiet, IZarBlockCompressor? compressor)
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
        if (comp == null && level != 6)
        {
            comp = new ZstdCompressor(ZstdCompressionOptions.FromLevel(level));
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
        int level, bool quiet, IZarBlockCompressor? compressor)
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
        Console.WriteLine();
        Console.WriteLine("Pack a directory to .zar, extract a .zar, or convert an XISO:");
        Console.WriteLine("  zar <directory> [output.zar]         Pack directory to .zar");
        Console.WriteLine("  zar <archive.zar> [output_dir]       Extract .zar to directory");
        Console.WriteLine("  zar --iso <game.iso> [output.zar]    Convert XISO to .zar");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -l, --level <N>       Compression level 1-22 (default: 6)");
        Console.WriteLine("  -j, --jobs <N>        Parallel workers (default: 4)");
        Console.WriteLine("  -p, --policy <P>      Collision policy: fail, skip, overwrite, auto-rename");
        Console.WriteLine("  -b, --batch           Batch process all files in input directory");
        Console.WriteLine("  -o, --output <path>   Output path");
        Console.WriteLine("  -q, --quiet           Suppress output");
        Console.WriteLine("      --no-compress     Store blocks without compression");
        Console.WriteLine("  -v, --version         Show version");
        Console.WriteLine("  -h, --help            Show this help");
    }
}
