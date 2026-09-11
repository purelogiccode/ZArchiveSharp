using ZArchiveSharp.Pipeline;
using ZArchiveSharp.Zstd;
#if HAS_XISO
using XISOSharp;
#endif

namespace ZArchiveSharp.Cli;

/// <summary>ZAR command-line entry point (pack, extract, XISO convert, batch).</summary>
public static class Program
{
    /// <summary>Runs the CLI and returns a <see cref="ZArchiveSharp.Pipeline.ZarchiveCli"/>-compatible exit code.</summary>
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
        bool policyExplicit = false;
        int level = 6;
        bool levelExplicit = false;
        bool quiet = false;
        bool noCompress = false;
        bool batch = false;
        string? dictPath = null;
        bool stdoutFlag = false;
        bool? checksumOverride = null;
        bool helpRequested = false;
        string? modeRaw = null;
        bool? keepOriginalsOverride = null;
        string? sevenZipRaw = null;

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
                    if (i + 1 < args.Length && int.TryParse(args[++i],
                            System.Globalization.CultureInfo.InvariantCulture, out int j)) jobs = j;
                    break;
                case "--policy" or "-p":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine(
                            "Error: missing value for --policy (expected fail, skip, overwrite, auto-rename).");
                        return ZarchiveCli.BadUsage;
                    }

                    policy = args[++i];
                    policyExplicit = true;
                    break;
                case "--level" or "-l":
                    if (i + 1 < args.Length && int.TryParse(args[++i],
                            System.Globalization.CultureInfo.InvariantCulture, out int lv))
                    {
                        level = lv;
                        levelExplicit = true;
                    }

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
                case "--mode":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine(
                            "Error: missing value for --mode (expected auto, extract-archive, extract-iso, compress).");
                        return ZarchiveCli.BadUsage;
                    }

                    modeRaw = args[++i];
                    break;
                case "--keep-originals":
                    keepOriginalsOverride = true;
                    break;
                case "--delete-source":
                    keepOriginalsOverride = false;
                    break;
                case "--seven-zip":
                    if (i + 1 >= args.Length)
                    {
                        Console.Error.WriteLine("Error: missing value for --seven-zip (expected a 7z binary path).");
                        return ZarchiveCli.BadUsage;
                    }

                    sevenZipRaw = args[++i];
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

        // value for every path, then let each non-batch consumer below reject
        // an explicit non-fail policy rather than silently ignoring it.
        ZarCollisionPolicy? collisionPolicy = policy.ToLowerInvariant() switch
        {
            "fail" => ZarCollisionPolicy.Fail,
            "skip" => ZarCollisionPolicy.Skip,
            "overwrite" => ZarCollisionPolicy.Overwrite,
            "auto-rename" or "autorename" => ZarCollisionPolicy.AutoRename,
            _ => null,
        };
        if (collisionPolicy == null)
        {
            Console.Error.WriteLine(
                $"Error: invalid --policy '{policy}' (expected fail, skip, overwrite, auto-rename).");
            return ZarchiveCli.BadUsage;
        }

        // New subcommand (additive; the positional pack/extract path below is
        // untouched). To pack/extract a path literally named "zstd", spell it
        // ./zstd so it is not taken for the subcommand.
        if (string.Equals(positional.FirstOrDefault(), "zstd", StringComparison.Ordinal))
        {
            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            var sub = positional.Skip(1).ToList();
            if (helpRequested)
            {
                sub.Add("--help");
            }

            return RunZstd(sub.ToArray(), level, dictPath, checksumOverride ?? false, quiet, stdoutFlag);
        }

        // Seekable zstd files (zeekstd framing). A path literally named
        // "seekable" must be spelled ./seekable to pack/extract it.
        if (string.Equals(positional.FirstOrDefault(), "seekable", StringComparison.Ordinal))
        {
            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            var sub = positional.Skip(1).ToList();
            if (helpRequested)
            {
                sub.Add("--help");
            }

            return RunSeekable(sub.ToArray(), level, levelExplicit, dictPath, checksumOverride, quiet, stdoutFlag);
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

        var processMode = ZarProcessMode.Auto;
        if (modeRaw != null)
        {
            if (!ZarProcessModes.TryParse(modeRaw, out processMode))
            {
                Console.Error.WriteLine(
                    $"Error: invalid --mode '{modeRaw}' (expected auto, extract-archive, extract-iso, compress).");
                return ZarchiveCli.BadUsage;
            }
        }

        bool deleteSource = keepOriginalsOverride == false;

        // --mode/--delete-source/--seven-zip/--policy select batch pipeline
        // auto-detects by input type, never deletes sources, and keeps the
        // zarchive.exe refuse-overwrite contract, so reject them there rather
        // than silently ignoring a destructive-looking request.
        if (!batch)
        {
            if (modeRaw != null)
            {
                Console.Error.WriteLine("Error: --mode is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }

            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            if (deleteSource)
            {
                Console.Error.WriteLine("Error: --delete-source is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }

            if (sevenZipRaw != null)
            {
                Console.Error.WriteLine("Error: --seven-zip is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }
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

        IZarBlockCompressor? compressor = noCompress ? new ZarRawCompressor() : null;

        // Mode 1: XISO → .zar
        if (isoPath != null)
        {
            return PackIso(isoPath, outputPath, level, quiet, compressor, dictionary);
        }

        // Mode 2: Batch operations
        if (batch)
        {
            return RunBatch(inputPath, outputPath, jobs, collisionPolicy.Value, level, quiet, compressor, dictionary,
                checksum, processMode, deleteSource, sevenZipRaw);
        }

        // Mode 3: Standard pack/extract (zarchive.exe compat)
        var options = new ZarPipelineOptions
        {
            Level = level,
            Checksum = checksum,
            Dictionary = dictionary,
            CollisionPolicy = collisionPolicy.Value,
            Compressor = compressor,
            MaxDegreeOfParallelism = jobs,
        };

        var args2 = new List<string>();
        if (inputPath != null) args2.Add(inputPath);
        if (outputPath != null) args2.Add(outputPath);

        return ZarchiveCli.Run(args2.ToArray(), options, log: quiet ? null : Console.WriteLine);
    }

    /// <summary>
    /// Rejects an explicit non-<c>fail</c> <c>--policy</c> off the batch path:
    /// single pack/extract/ISO and the zstd/seekable subcommands keep the
    /// refuse-overwrite contract, so the flag must fail loud, never silently.
    /// </summary>
    private static int RejectNonBatchPolicy(string policy)
    {
        Console.Error.WriteLine($"Error: --policy {policy} is only supported with --batch.");
        return ZarchiveCli.BadUsage;
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
            : job.ShowHelp || job.OutputPath is not null
                ? Console.WriteLine
                : Console.Error.WriteLine;

        using var cts = new CancellationTokenSource();
        // Safe: unsubscribed in finally before cts is disposed at scope end.
        // ReSharper disable once AccessToDisposedClosure
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
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

    private static int RunSeekable(
        string[] seekableArgs, int globalLevel, bool levelExplicit, string? dictPath,
        bool? checksumOverride, bool quiet, bool stdoutFlag)
    {
        // Seekable frames carry no zstd dictionary (and the diff-engine
        // --patch-from has no library support), so an explicit --dict with
        // this subcommand is a usage error, not a silent ignore.
        if (dictPath != null)
        {
            Console.Error.WriteLine(
                "Error: --dict is not supported with 'zar seekable' (seekable frames carry no dictionary).");
            return ZarchiveCli.BadUsage;
        }

        if (!SeekableCli.TryParse(seekableArgs, out var job, out string? parseError,
                defaultLevel: levelExplicit ? globalLevel : 3, defaultChecksum: checksumOverride,
                defaultQuiet: quiet, defaultStdout: stdoutFlag))
        {
            Console.Error.WriteLine($"Error: {parseError}");
            Console.Error.WriteLine(SeekableCli.UsageText);
            return ZarchiveCli.BadUsage;
        }

        // Binary output to stdout: informational lines must go to stderr or
        // they would corrupt piped data. List output IS the table, so it
        // always goes to stdout (quiet is ignored there, like the oracle).
        var parsed = job!;
        Action<string>? log = parsed switch
        {
            { ShowHelp: true } => Console.WriteLine,
            { Command: SeekableCli.SeekableCommand.List } => Console.WriteLine,
            { Quiet: true } => null,
            { OutputPath: not null } => Console.WriteLine,
            _ => Console.Error.WriteLine,
        };

        using var cts = new CancellationTokenSource();
        // Safe: unsubscribed in finally before cts is disposed at scope end.
        // ReSharper disable once AccessToDisposedClosure
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return SeekableCli.RunAsync(parsed, Console.OpenStandardInput(), Console.OpenStandardOutput(),
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

#if HAS_XISO
    /// <summary>
    /// Resolves the XISO game-partition offset for an ISO file: 0 for plain
    /// XISOs, the wave-dependent <c>XgdTables.XisoOffset</c> for Redump ISOs
    /// (recognized by exact file size). Mirrors the <c>--zar</c> resolution in
    /// <c>XISOSharp.Cli/Program.cs</c>, including its fallbacks: an unreadable
    /// wave PVD resolves as video type 0, and an out-of-range XISO type falls
    /// back to the Redump type's XGD mapping. Never throws: stat/read faults
    /// resolve to 0 and the pack surfaces the real error.
    /// </summary>
    private static long ResolveGamePartitionOffset(string isoPath, bool quiet)
    {
        try
        {
            long size = new FileInfo(isoPath).Length;
            int redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            if (redumpType < 0)
            {
                return 0;
            }

            // Wave-dependent sizes (types 5 and 7) need the PVD at 0x832D;
            // other Redump types map straight through (null stream is fine:
            // GetWave returns -1 without a stream and the switch ignores it).
            int videoType = -1;
            try
            {
                using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                videoType = XgdTables.GetVideoType(fs, redumpType);
            }
            catch
            {
                // ignored: fall back below, like XISOSharp.Cli.
            }

            int vType = videoType >= 0 ? videoType : 0;
            int xsType = XgdTables.GetXisoTypeFromVideo(vType);
            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
            {
                xsType = XgdTables.GetXgdType(redumpType);
            }

            long isoOffset = XgdTables.XisoOffset[xsType];
            if (!quiet)
            {
                string video =
 videoType >= 0 ? videoType.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown (assuming 0)";
                Console.WriteLine($"Redump ISO detected (type {redumpType}, video {video}); game partition at 0x{isoOffset:X}.");
            }

            return isoOffset;
        }
        catch
        {
            return 0;
        }
    }
#endif

    /// <summary>
    /// Converts one ISO to <paramref name="destZar"/> (Redump-aware: the game
    /// partition offset is resolved first, like single <c>--iso</c>). Single
    /// `--iso` and the batch ISO/archive legs share this; it never writes
    /// to the console when <paramref name="quiet"/> is true. Returns true on
    /// success with <paramref name="error"/> null, else false with a reason.
    /// </summary>
    private static bool TryPackIso(string isoPath, string destZar, int level, bool quiet,
        IZarBlockCompressor? compressor, ZstdDictionary? dictionary,
        IProgress<ZarProgress>? progress, out string? error)
    {
        error = null;
        if (!File.Exists(isoPath))
        {
            error = $"ISO file not found: {isoPath}";
            return false;
        }

#if !HAS_XISO
        error = "unavailable: this build of zar was compiled without XISOSharp support. " +
                "Clone https://github.com/purelogiccode/XISOSharp.git as a sibling 'CSharp_XISOSharp' directory and rebuild for XISO support.";
        return false;
#else
        // Redump ISOs start with the video partition: the XISO game partition
        // sits at a wave-dependent offset (XISOSharp.Cli --zar resolves it the
        // same way from the same XgdTables). Packing at offset 0 would archive
        // the video area as garbage, so resolve the game offset first.
        long isoOffset = ResolveGamePartitionOffset(isoPath, quiet);

        if (!quiet) Console.WriteLine($"Converting XISO to ZAR: {isoPath} -> {destZar}");

        IZarBlockCompressor? comp = compressor;
        if (comp == null && (level != 6 || dictionary != null))
        {
            comp = new ZstdCompressor(new ZstdCompressionOptions { Level = level, Dictionary = dictionary });
        }

        try
        {
            bool ok =
 XisoZarchive.CreateZar(isoPath, destZar, isoOffset, quiet: quiet, compressor: comp, progress: progress);
            if (!quiet) Console.WriteLine();
            if (ok)
            {
                if (!quiet) Console.WriteLine($"Done: {destZar}");
                return true;
            }

            error = "XISO conversion failed.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
#endif
    }

    private static int PackIso(string isoPath, string? zarPath, int level, bool quiet, IZarBlockCompressor? compressor,
        ZstdDictionary? dictionary)
    {
        if (!File.Exists(isoPath))
        {
            Console.Error.WriteLine($"Error: ISO file not found: {isoPath}");
            return -10;
        }

#if !HAS_XISO
        Console.Error.WriteLine(
            "Error: --iso is unavailable: this build of zar was compiled without XISOSharp support.");
        Console.Error.WriteLine(
            "Clone https://github.com/purelogiccode/XISOSharp.git as a sibling 'CSharp_XISOSharp' directory and rebuild for XISO support.");
        return ZarchiveCli.BadUsage;
#else
        string output = zarPath ?? DeriveZarPath(isoPath);

        if (File.Exists(output))
        {
            Console.Error.WriteLine($"Error: Output file already exists: {output}");
            return -11;
        }

        var progress = quiet ? null : new Progress<ZarProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                double pct = (double)p.BytesCompleted / p.BytesTotal * 100;
                Console.Write($"\r  {pct:F1}% ({p.BytesCompleted / (1024 * 1024)} / {p.BytesTotal / (1024 * 1024)} MiB)");
            }
        });

        if (TryPackIso(isoPath, output, level, quiet, compressor, dictionary, progress, out string? error))
        {
            return 0;
        }

        Console.Error.WriteLine($"Error: {error}");
        return -13;
#endif
    }

    private static int RunBatch(string? inputPath, string? outputPath, int jobs, ZarCollisionPolicy policy,
        int level, bool quiet, IZarBlockCompressor? compressor, ZstdDictionary? dictionary, bool checksum,
        ZarProcessMode mode, bool deleteSource, string? sevenZipPath)
    {
        if (inputPath == null || !Directory.Exists(inputPath))
        {
            Console.Error.WriteLine("Error: --batch requires an input directory.");
            return -1;
        }

        string destDir = outputPath ?? inputPath;

        var files = ProcessableFiles.Find(inputPath, mode);
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
            DeleteSourceOnSuccess = deleteSource,
        };

        var progress = quiet
            ? null
            : new Progress<ZarProgress>(p =>
            {
                if (p.Operation == ZarOperation.Pack)
                {
                    double pct = p.Ratio * 100;
                    Console.Write(
                        $"\r  [{p.FilesCompleted}/{p.FilesTotal}] {pct:F1}% {Path.GetFileName(p.SourcePath)}");
                }
            });

        try
        {
            Directory.CreateDirectory(destDir);

            // Partition by kind: directories pack as-is, ISOs convert
            // straight to .zar, archives run the 7z container stage first
            var dirs = new List<string>();
            var isos = new List<string>();
            var archives = new List<string>();
            foreach (var file in files)
            {
                if (Directory.Exists(file))
                {
                    dirs.Add(file);
                }
                else if (ProcessableFiles.IsoExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                {
                    isos.Add(file);
                }
                else
                {
                    archives.Add(file);
                }
            }

            if (sevenZipPath != null && archives.Count > 0 && !File.Exists(sevenZipPath))
            {
                Console.Error.WriteLine($"Error: 7z binary not found: {sevenZipPath}");
                return -1;
            }

            var results = new List<ZarItemResult>();
            if (dirs.Count > 0)
            {
                results.AddRange(ZarPipeline.PackBatch(dirs, destDir, options, progress));
            }

            if (isos.Count > 0)
            {
                results.AddRange(PackIsoBatch(isos, destDir, options, level, compressor, dictionary, progress));
            }

            if (archives.Count > 0)
            {
                results.AddRange(ProcessArchiveBatch(archives, destDir, options, mode, level,
                    quiet, compressor, dictionary, sevenZipPath, jobs, progress));
            }

            if (!quiet) Console.WriteLine();

            int ok = 0, fail = 0, skip = 0;
            foreach (var r in results)
            {
                if (r.Status == ZarItemStatus.Completed)
                {
                    ok++;
                }
                else if (r.Status == ZarItemStatus.Skipped)
                {
                    skip++;
                    if (!quiet) Console.WriteLine($"  Skipped: {r.SourcePath} - {r.ErrorMessage}");
                }
                else
                {
                    fail++;
                    Console.Error.WriteLine($"  Failed: {r.SourcePath} - {r.ErrorMessage}");
                }
            }

            if (!quiet)
            {
                Console.WriteLine(skip == 0
                    ? $"Batch complete: {ok} succeeded, {fail} failed."
                    : $"Batch complete: {ok} succeeded, {fail} failed, {skip} skipped.");
            }

            return fail > 0 ? -13 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return -13;
        }
    }

    /// <summary>
    /// Converts each ISO in <paramref name="isos"/> to a <c>.zar</c> next to
    /// <c>extract</c> + <c>compress</c> stages fused, since this CLI packs an
    /// ISO straight to <c>.zar</c> without an intermediate directory).
    /// </summary>
    private static IReadOnlyList<ZarItemResult> PackIsoBatch(IReadOnlyList<string> isos, string destDir,
        ZarPipelineOptions options, int level, IZarBlockCompressor? compressor, ZstdDictionary? dictionary,
        IProgress<ZarProgress>? progress)
    {
        var list = isos.ToList();
        var results = new ZarItemResult?[list.Count];
        Parallel.For(0, list.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Math.Max(1, options.MaxDegreeOfParallelism), Math.Max(1, list.Count)),
            },
            i => results[i] = PackIsoOne(list[i], destDir, options, level, compressor, dictionary, progress));
        return results.Select((r, i) => r ??
                                        new ZarItemResult(list[i], null, ZarItemStatus.Cancelled,
                                            "Cancelled before start.")).ToList();
    }

    private static ZarItemResult PackIsoOne(string iso, string destDir, ZarPipelineOptions options,
        int level, IZarBlockCompressor? compressor, ZstdDictionary? dictionary,
        IProgress<ZarProgress>? progress)
    {
        string dest = Path.Combine(destDir, Path.GetFileName(DeriveZarPath(iso)));
        try
        {
            string? resolved = ZarPackEngine.ResolveOutputPath(dest, options.CollisionPolicy);
            if (resolved == null)
            {
                return new ZarItemResult(iso, dest, ZarItemStatus.Skipped, "Output already exists.");
            }

            if (TryPackIso(iso, resolved, level, quiet: true, compressor, dictionary, progress, out string? error))
            {
                if (options.DeleteSourceOnSuccess)
                {
                    File.Delete(iso);
                }

                return new ZarItemResult(iso, resolved, ZarItemStatus.Completed);
            }

            return new ZarItemResult(iso, dest, ZarItemStatus.Failed, error);
        }
        catch (OperationCanceledException ex)
        {
            return new ZarItemResult(iso, dest, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException)
        {
            // Batch isolation like ZarPipeline.PackOne: log the item, run the rest.
            return new ZarItemResult(iso, dest, ZarItemStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Runs the archive-container stage over <paramref name="archives"/>
    /// <c>destDir/temp_&lt;stem&gt;</c>; the first <c>.iso</c> found keeps
    /// going down the pipeline as <c>&lt;stem&gt;.iso</c>, otherwise the
    /// whole tree becomes <c>&lt;stem&gt;/</c>. Under
    /// <see cref="ZarProcessMode.ExtractArchive"/> the item stops there;
    /// under <see cref="ZarProcessMode.Auto"/> it continues to the ISO or
    /// directory leg, ending at <c>.zar</c> like the oracle.
    /// </summary>
    private static IReadOnlyList<ZarItemResult> ProcessArchiveBatch(IReadOnlyList<string> archives, string destDir,
        ZarPipelineOptions options, ZarProcessMode mode, int level, bool quiet,
        IZarBlockCompressor? compressor, ZstdDictionary? dictionary, string? sevenZipPath,
        int jobs, IProgress<ZarProgress>? progress)
    {
        var list = archives.ToList();
        var results = new ZarItemResult?[list.Count];
        Parallel.For(0, list.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Math.Max(1, jobs), Math.Max(1, list.Count)),
            },
            i => results[i] = ProcessArchiveOne(list[i], destDir, options, mode, level,
                quiet, compressor, dictionary, sevenZipPath, progress));
        return results.Select((r, i) => r ??
                                        new ZarItemResult(list[i], null, ZarItemStatus.Cancelled,
                                            "Cancelled before start.")).ToList();
    }

    private static ZarItemResult ProcessArchiveOne(string archive, string destDir, ZarPipelineOptions options,
        ZarProcessMode mode, int level, bool quiet, IZarBlockCompressor? compressor,
        ZstdDictionary? dictionary, string? sevenZipPath, IProgress<ZarProgress>? progress)
    {
        string stem = Path.GetFileNameWithoutExtension(archive);
        string? temp = Path.Combine(destDir, $"temp_{stem}");
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            Directory.CreateDirectory(temp);

            string? tool = !string.IsNullOrWhiteSpace(sevenZipPath) ? sevenZipPath : SevenZip.FindTool();
            if (tool == null)
            {
                return new ZarItemResult(archive, null, ZarItemStatus.Failed,
                    "No 7z binary found. Install 7-Zip and ensure 7z is on PATH, or pass --seven-zip <path>.");
            }

            if (!quiet) Console.WriteLine($"Extracting archive: {archive}");
            IProgress<double>? sevenProgress = quiet
                ? null
                : new Progress<double>(ratio =>
                    Console.Write($"\r  {ratio * 100:F1}% {stem}"));
            SevenZip.Extract(archive, temp, tool, sevenProgress, options.Pause);
            if (!quiet) Console.WriteLine();

            var extracted = Directory.EnumerateFileSystemEntries(temp, "*", SearchOption.AllDirectories).ToList();
            string? current;
            bool isIso;
            string? iso = SevenZip.PickIsoCandidate(extracted.Where(File.Exists));
            if (iso != null)
            {
                string? moved = ZarPackEngine.ResolveOutputPath(
                    Path.Combine(destDir, stem + ".iso"), options.CollisionPolicy);
                if (moved == null)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Skipped, "Output already exists.");
                }

                File.Move(iso, moved);
                current = moved;
                isIso = true;
            }
            else
            {
                if (extracted.Count == 0)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Failed, "Archive yielded no files.");
                }

                string? moved = ZarPackEngine.ResolveOutputPath(
                    Path.Combine(destDir, stem), options.CollisionPolicy);
                if (moved == null)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Skipped, "Output already exists.");
                }

                Directory.Move(temp, moved);
                temp = null;
                current = moved;
                isIso = false;
            }

            if (options.DeleteSourceOnSuccess)
            {
                File.Delete(archive);
            }

            if (mode == ZarProcessMode.ExtractArchive)
            {
                return new ZarItemResult(archive, current, ZarItemStatus.Completed);
            }

            // Continue down the pipeline; re-stamp the source so the batch
            // summary names the archive the user asked about, not the
            // intermediate the stage produced.
            if (isIso)
            {
                return PackIsoOne(current, destDir, options, level, compressor, dictionary, progress)
                    with
                    {
                        SourcePath = archive
                    };
            }

            return ZarPipeline.PackBatch([current], destDir, options, progress)[0]
                with
                {
                    SourcePath = archive
                };
        }
        catch (OperationCanceledException ex)
        {
            return new ZarItemResult(archive, null, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException)
        {
            return new ZarItemResult(archive, null, ZarItemStatus.Failed, ex.Message);
        }
        finally
        {
            // Best effort like shutil.rmtree(ignore_errors=True): a moved
            // tree nulls temp; leftovers must never fail the item.
            try
            {
                if (temp != null && Directory.Exists(temp))
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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

        // MinVer stamps e.g. "1.0.0+githash" ("0.0.0-alpha.0.N+githash" with no tag); keep the version core.
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
        Console.WriteLine("       zar seekable compress|decompress|list [options] [input] [output]");
        Console.WriteLine();
        Console.WriteLine("Pack a directory to .zar, extract a .zar, convert an XISO, or");
        Console.WriteLine("compress/decompress single zstd streams:");
        Console.WriteLine("  zar <directory> [output.zar]         Pack directory to .zar");
        Console.WriteLine("  zar <archive.zar> [output_dir]       Extract .zar to directory");
        Console.WriteLine("  zar --iso <game.iso> [output.zar]    Convert XISO to .zar");
#if HAS_XISO
        Console.WriteLine("                                     (Redump ISOs auto-detected: packs");
        Console.WriteLine("                                     the game partition, not the video)");
#else
        Console.WriteLine("                                     (unavailable in this build:");
        Console.WriteLine("                                     compiled without XISOSharp)");
#endif
        Console.WriteLine("  zar zstd -c [in] [out]               Compress a file/stdin to zstd");
        Console.WriteLine("  zar zstd -d [in] [out]               Decompress a zstd file/stdin");
        Console.WriteLine("  zar seekable compress [in] [out]     Compress to seekable .zst");
        Console.WriteLine("  zar seekable decompress [in] [out]   Decompress seekable .zst");
        Console.WriteLine("  zar seekable list <file>             Show seekable frame table");
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
        Console.WriteLine("                        (only with --batch; single paths always refuse)");
        Console.WriteLine("  -b, --batch           Batch process all files in input directory");
        Console.WriteLine("      --mode <M>        Batch stages (default: auto; only with --batch):");
        Console.WriteLine("                        auto: archives-(7z)-ISO/dir-.zar, ISOs-.zar, dirs-.zar");
        Console.WriteLine("                        extract-archive: extract archives with 7z, stop there");
        Console.WriteLine("                        extract-iso: convert ISOs to .zar;");
        Console.WriteLine("                        compress: pack directories to .zar");
        Console.WriteLine("      --seven-zip <exe> 7z binary for the archive stage (default: PATH plus");
        Console.WriteLine("                        the standard install location; only with --batch)");
        Console.WriteLine("      --keep-originals  Keep batch sources after packing (default; last wins");
        Console.WriteLine("                        against --delete-source; only with --batch)");
        Console.WriteLine("      --delete-source   Delete each batch source dir after its pack succeeds");
        Console.WriteLine("                        (only with --batch)");
        Console.WriteLine("  -o, --output <path>   Output path");
        Console.WriteLine("  -q, --quiet           Suppress output");
        Console.WriteLine("      --no-compress     Store blocks without compression");
        Console.WriteLine("  -v, --version         Show version");
        Console.WriteLine("  -h, --help            Show this help");
        Console.WriteLine();
        Console.WriteLine("Run 'zar zstd --help' for the zstd subcommand. A path literally");
        Console.WriteLine("named 'zstd' must be spelled ./zstd to pack/extract it.");
        Console.WriteLine("Run 'zar seekable --help' for the seekable subcommand (same");
        Console.WriteLine("./seekable escape for a colliding path).");
    }
}