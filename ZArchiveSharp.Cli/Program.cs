using Serilog;
using Serilog.Events;
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
        using var bugSink = new BugReportSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new CliConsoleSink())
            .WriteTo.Sink(bugSink, LogEventLevel.Warning)
            .CreateLogger();
        var (quiet, informational) = ScanGlobalFlags(args);
        if (HasOptionBeforeTerminator(args, "--no-telemetry"))
        {
            BugReportSink.DisableTelemetry();
        }

        // Usage stats and the update check are opt-out telemetry:
        // --no-telemetry and ZAR_BUG_REPORT=off disable every outbound call,
        // and --help/--version launches never phone home.
        if (!informational)
        {
            UsageTracker.TrackLaunch();
        }

        var updateCheck = informational
            ? Task.FromResult<UpdateChecker.ReleaseInfo?>(null)
            : UpdateChecker.Begin();
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            CliLog.Fatal("Unhandled exception.", ex);
            return ZarchiveCli.PackFailed; // generic internal failure; no closer oracle code exists
        }
        finally
        {
            // Bounded best-effort telemetry flush: a slow/unreachable
            // endpoint must not delay exit (the senders are background
            // tasks, so the process is free to go once the budget is spent).
            bugSink.Flush(TimeSpan.FromMilliseconds(500));
            UsageTracker.WaitForPendingSend(TimeSpan.FromMilliseconds(250));
            Log.CloseAndFlush();
            UpdateChecker.Notify(updateCheck, quiet);
            ConsoleWindow.KeepOpenIfOwned(quiet);
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string? isoPath = null;
        string? inputPath = null;
        string? outputPath = null;
        var outputExplicit = false;
        var jobs = 4;
        var policy = "fail";
        var policyExplicit = false;
        var level = 6;
        var levelExplicit = false;
        var quiet = false;
        var noCompress = false;
        var batch = false;
        string? dictPath = null;
        var stdoutFlag = false;
        var zstdCompressFlag = false;
        bool? checksumOverride = null;
        string? checksumOption = null;
        string? levelOption = null;
        var helpRequested = false;
        string? modeRaw = null;
        bool? keepOriginalsOverride = null;
        string? sevenZipRaw = null;

        var positional = new List<string>();
        // -c is resolved after parsing: it means --compress when the first
        // positional is a real (pre-terminator) zstd subcommand token, and
        // the global --stdout everywhere else.
        var dashC = false;
        var sawTerminator = false;
        var terminatorAt = -1;
        var firstPositionalEligible = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--iso" or "-i":
                    if (!TryTakeValue(args, ref i, args[i], "an ISO file", out var isoValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    isoPath = isoValue;
                    break;
                case "--batch" or "-b":
                    batch = true;
                    break;
                case "--output" or "-o":
                    if (!TryTakeValue(args, ref i, args[i], "a path", out var outputValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    outputPath = outputValue;
                    outputExplicit = true;
                    break;
                case "--jobs" or "-j":
                    if (!TryTakeValue(args, ref i, args[i], "a positive integer", out var jobsRaw))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    if (!int.TryParse(jobsRaw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var j) || j < 1)
                    {
                        CliLog.Err($"Error: invalid --jobs '{jobsRaw}' (expected a positive integer).");
                        return ZarchiveCli.BadUsage;
                    }

                    jobs = j;
                    break;
                case "--policy" or "-p":
                    if (!TryTakeValue(args, ref i, args[i], "fail, skip, overwrite, auto-rename", out var policyValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    policy = policyValue;
                    policyExplicit = true;
                    break;
                case "--level" or "-l":
                    if (!TryTakeValue(args, ref i, args[i], "1-22", out var levelRaw))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    if (!int.TryParse(levelRaw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var lv) || lv < 1 || lv > 22)
                    {
                        CliLog.Err($"Error: invalid --level '{levelRaw}' (expected 1-22).");
                        return ZarchiveCli.BadUsage;
                    }

                    level = lv;
                    levelExplicit = true;
                    levelOption = args[i];
                    break;
                case "--dict":
                    if (!TryTakeValue(args, ref i, args[i], "a dictionary file", out var dictValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    dictPath = dictValue;
                    break;
                case "--stdout":
                    stdoutFlag = true;
                    break;
                case "-c":
                    // Resolved after the loop (the subcommand token may not
                    // have been seen yet): compress inside `zar zstd`, the
                    // global --stdout everywhere else.
                    dashC = true;
                    break;
                case "--check":
                    checksumOverride = true;
                    checksumOption = args[i];
                    break;
                case "--no-check":
                    checksumOverride = false;
                    checksumOption = args[i];
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--mode":
                    if (!TryTakeValue(args, ref i, args[i], "auto, extract-archive, extract-iso, compress",
                            out var modeValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    modeRaw = modeValue;
                    break;
                case "--keep-originals":
                    keepOriginalsOverride = true;
                    break;
                case "--delete-source":
                    keepOriginalsOverride = false;
                    break;
                case "--seven-zip":
                    if (!TryTakeValue(args, ref i, args[i], "a 7z binary path", out var sevenZipValue))
                    {
                        return ZarchiveCli.BadUsage;
                    }

                    sevenZipRaw = sevenZipValue;
                    break;
                case "--no-compress":
                    noCompress = true;
                    break;
                case "--no-telemetry":
                    // Applied process-wide in Main, before any telemetry
                    // starts; accepted here so it is not a usage error.
                    break;
                case "--help" or "-h":
                    helpRequested = true;
                    break;
                case "--version" or "-v":
                    CliLog.Out($"{ProgramName} {GetVersion()}");
                    return 0;
                case "--":
                    // End of options: everything after is positional, so
                    // paths that begin with '-' stay reachable. The position
                    // is recorded so subcommand dispatch can forward the
                    // terminator to the subcommand's own parser as well.
                    sawTerminator = true;
                    terminatorAt = positional.Count;
                    positional.AddRange(args[(i + 1)..]);
                    i = args.Length;
                    break;
                default:
                    // Unknown options are usage errors for the plain
                    // pack/extract shape instead of silently becoming paths.
                    // After a (pre-terminator) zstd/seekable token they are
                    // forwarded to that subcommand's own parser (which owns
                    // its option set).
                    if (args[i].StartsWith('-') && args[i].Length > 1
                        && !InSubcommandArguments(positional, firstPositionalEligible))
                    {
                        CliLog.Err($"Error: unknown option '{args[i]}'.");
                        return ZarchiveCli.BadUsage;
                    }

                    if (positional.Count == 0 && !sawTerminator)
                    {
                        firstPositionalEligible = true;
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        // -c is --compress inside a real `zar zstd` invocation and the global
        // --stdout otherwise (the subcommand token may appear after -c).
        if (dashC)
        {
            if (firstPositionalEligible && string.Equals(positional.FirstOrDefault(), "zstd", StringComparison.Ordinal))
            {
                zstdCompressFlag = true;
            }
            else
            {
                stdoutFlag = true;
            }
        }

        // --iso converts one ISO; --batch takes an input directory. Combining
        // them would silently ignore the batch flag (and any batch-only
        // flags), so fail loud instead.
        if (isoPath is not null && batch)
        {
            CliLog.Err("Error: --iso cannot be combined with --batch (--batch takes an input directory).");
            return ZarchiveCli.BadUsage;
        }

        // Positional contract (zarchive.exe parity + -o/--iso extensions):
        // oracle args are exactly `input [output]`. An explicit -o already
        // occupies the output slot, so at most one positional (the input)
        // remains; --iso takes no input positional (its input is the flag
        // value), so at most one positional (the output) remains there.
        // Anything beyond is a usage error, never a silent drop.
        if (isoPath is not null)
        {
            if (outputExplicit)
            {
                if (positional.Count > 0)
                {
                    CliLog.WarnToStdout("Too many paths specified");
                    return ZarchiveCli.BadUsage;
                }
            }
            else
            {
                if (positional.Count > 1)
                {
                    CliLog.WarnToStdout("Too many paths specified");
                    return ZarchiveCli.BadUsage;
                }

                if (positional.Count == 1)
                {
                    outputPath = positional[0];
                }
            }
        }
        else
        {
            if (outputExplicit)
            {
                if (positional.Count == 0)
                {
                    // Without an input path the output would be dispatched as
                    // the input (e.g. `zar -o game.zar` would extract it or
                    // pack it): fail loud instead of running the wrong op.
                    CliLog.Err($"Error: missing input path (expected: {ProgramName} [options] [input] [output]).");
                    return ZarchiveCli.BadUsage;
                }

                if (positional.Count > 1)
                {
                    CliLog.WarnToStdout("Too many paths specified");
                    return ZarchiveCli.BadUsage;
                }

                if (inputPath == null)
                {
                    inputPath = positional[0];
                }
            }
            else
            {
                if (positional.Count > 0 && inputPath == null)
                {
                    inputPath = positional[0];
                }

                if (positional.Count > 1 && outputPath == null)
                {
                    outputPath = positional[1];
                }
            }
        }

        // --policy is a batch-pipeline knob: validate the value for every
        // path, then let each non-batch consumer below reject an explicit
        // non-fail policy rather than silently ignoring it.
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
            CliLog.Err(
                $"Error: invalid --policy '{policy}' (expected fail, skip, overwrite, auto-rename).");
            return ZarchiveCli.BadUsage;
        }

        // New subcommand (additive; the positional pack/extract path below is
        // untouched). To pack/extract a path literally named "zstd", spell it
        // ./zstd (or put it after --) so it is not taken for the subcommand.
        if (firstPositionalEligible && string.Equals(positional.FirstOrDefault(), "zstd", StringComparison.Ordinal))
        {
            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            var sub = BuildSubcommandArgs(positional, sawTerminator, terminatorAt, zstdCompressFlag, helpRequested);
            return RunZstd(sub, level, dictPath, checksumOverride, quiet, stdoutFlag);
        }

        // Seekable zstd files (zeekstd framing). A path literally named
        // "seekable" must be spelled ./seekable (or put it after --) to
        // pack/extract it.
        if (firstPositionalEligible && string.Equals(positional.FirstOrDefault(), "seekable", StringComparison.Ordinal))
        {
            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            var sub = BuildSubcommandArgs(positional, sawTerminator, terminatorAt, insertCompress: false, helpRequested);
            return RunSeekable(sub, level, levelExplicit, levelOption, dictPath, checksumOverride,
                checksumOption, quiet, stdoutFlag);
        }

        if (helpRequested)
        {
            PrintUsage();
            return 0;
        }

        // Final guard for the plain pack/extract/batch shape (no -o/--iso):
        // exactly `input_path [output_path]`; a third positional is a usage
        // error, never a silently ignored extra. The -o/--iso shapes above
        // already enforce their tighter (≤1 / 0) limits. Subcommands above
        // already consumed their own positionals.
        // (same message and stdout channel as the oracle and ZarchiveCli).
        if (positional.Count > 2)
        {
            CliLog.WarnToStdout("Too many paths specified");
            return ZarchiveCli.BadUsage;
        }

        if (stdoutFlag)
        {
            CliLog.Err(
                $"Error: --stdout is only supported with the '{ProgramName} zstd' and '{ProgramName} seekable' subcommands.");
            return ZarchiveCli.BadUsage;
        }

        var processMode = ZarProcessMode.Auto;
        if (modeRaw != null)
        {
            if (!ZarProcessModes.TryParse(modeRaw, out processMode))
            {
                CliLog.Err(
                    $"Error: invalid --mode '{modeRaw}' (expected auto, extract-archive, extract-iso, compress).");
                return ZarchiveCli.BadUsage;
            }
        }

        var deleteSource = keepOriginalsOverride == false;

        // --mode/--delete-source/--seven-zip/--policy select batch pipeline
        // stages; the single pack/extract path below auto-detects by input
        // type, never deletes sources, and keeps the zarchive.exe
        // refuse-overwrite contract, so reject them there rather than
        // silently ignoring a destructive-looking request.
        if (!batch)
        {
            if (modeRaw != null)
            {
                CliLog.Err("Error: --mode is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }

            if (policyExplicit && collisionPolicy != ZarCollisionPolicy.Fail)
            {
                return RejectNonBatchPolicy(policy);
            }

            if (deleteSource)
            {
                CliLog.Err("Error: --delete-source is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }

            if (sevenZipRaw != null)
            {
                CliLog.Err("Error: --seven-zip is only supported with --batch.");
                return ZarchiveCli.BadUsage;
            }
        }

        // --no-compress stores raw blocks and ignores --dict (docs): skip
        // loading entirely so a missing/stale dictionary path cannot fail a
        // run whose compression never uses it.
        ZstdDictionary? dictionary = null;
        if (dictPath != null && !noCompress)
        {
            dictionary = TryLoadDictionary(dictPath);
            if (dictionary == null)
            {
                return ZarchiveCli.BadUsage;
            }
        }

        var checksum = checksumOverride ?? false;

        IZarBlockCompressor? compressor = noCompress ? new ZarRawCompressor() : null;

        // Mode 1: XISO → .zar
        if (isoPath != null)
        {
            return PackIso(isoPath, outputPath, level, quiet, compressor, dictionary, checksum);
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

        return ZarchiveCli.Run(args2.ToArray(), options, log: quiet ? null : CliLog.Out);
    }

    /// <summary>
    /// Rejects an explicit non-<c>fail</c> <c>--policy</c> off the batch path:
    /// single pack/extract/ISO and the zstd/seekable subcommands keep the
    /// refuse-overwrite contract, so the flag must fail loud, never silently.
    /// </summary>
    private static int RejectNonBatchPolicy(string policy)
    {
        CliLog.Err($"Error: --policy {policy} is only supported with --batch.");
        return ZarchiveCli.BadUsage;
    }

    /// <summary>
    /// True when <paramref name="option"/> appears before a <c>--</c>
    /// terminator (after it, the token is a positional path).
    /// </summary>
    private static bool HasOptionBeforeTerminator(string[] args, string option)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(arg, option, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects the launch-level flags (<c>--quiet</c>, <c>--help</c>/
    /// <c>--version</c>) the way the parser does: value-taking options are
    /// skipped with their values and <c>--</c> ends the scan, so an option
    /// value that happens to look like <c>-q</c>/<c>-v</c> is not
    /// misclassified as a launch flag.
    /// </summary>
    private static (bool Quiet, bool Informational) ScanGlobalFlags(string[] args)
    {
        var quiet = false;
        var informational = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--", StringComparison.Ordinal))
            {
                break;
            }

            switch (arg)
            {
                case "--help" or "-h" or "--version" or "-v":
                    informational = true;
                    break;
                case "--quiet" or "-q":
                    quiet = true;
                    break;
                case "--iso" or "-i" or "--output" or "-o" or "--jobs" or "-j" or "--policy" or "-p"
                    or "--level" or "-l" or "--dict" or "--mode" or "--seven-zip":
                    i++; // Skip the option's value.
                    break;
            }
        }

        return (quiet, informational);
    }

    /// <summary>
    /// True once the first positional is a zstd/seekable subcommand token
    /// (not a path after <c>--</c>): unknown dashed tokens are forwarded to
    /// that subcommand's parser from then on.
    /// </summary>
    private static bool InSubcommandArguments(List<string> positional, bool firstPositionalEligible)
    {
        if (!firstPositionalEligible || positional.Count == 0)
        {
            return false;
        }

        return string.Equals(positional[0], "zstd", StringComparison.Ordinal)
               || string.Equals(positional[0], "seekable", StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the argument list forwarded to a zstd/seekable subcommand:
    /// drops the subcommand token, re-inserts the <c>--</c> terminator at its
    /// original relative position (so dashed paths after it stay reachable),
    /// and prepends <c>-c</c>/<c>--help</c> before the terminator so they are
    /// still parsed as options.
    /// </summary>
    private static string[] BuildSubcommandArgs(
        List<string> positional, bool sawTerminator, int terminatorAt, bool insertCompress, bool helpRequested)
    {
        var sub = positional.Skip(1).ToList();
        if (sawTerminator && terminatorAt >= 1)
        {
            sub.Insert(terminatorAt - 1, "--");
        }

        if (insertCompress)
        {
            sub.Insert(0, "-c");
        }

        if (helpRequested)
        {
            sub.Insert(0, "--help");
        }

        return sub.ToArray();
    }

    /// <summary>
    /// Reads the value of a value-taking global option. A missing value or a
    /// following known option (<c>zar --jobs --quiet in out</c>) is a usage
    /// error instead of silently swallowing the next flag; other
    /// dash-prefixed values (paths like <c>-game.iso</c>) stay reachable.
    /// </summary>
    private static bool TryTakeValue(string[] args, ref int index, string option, string expected, out string value)
    {
        if (index + 1 >= args.Length || IsKnownGlobalOption(args[index + 1]))
        {
            CliLog.Err($"Error: missing value for {option} (expected {expected}).");
            value = string.Empty;
            return false;
        }

        value = args[index + 1];
        index++;
        return true;
    }

    /// <summary>True for every option token the global parser understands.</summary>
    private static bool IsKnownGlobalOption(string token)
    {
        return token is "--iso" or "-i" or "--batch" or "-b" or "--output" or "-o" or "--jobs" or "-j"
            or "--policy" or "-p" or "--level" or "-l" or "--dict" or "--stdout" or "-c" or "--check"
            or "--no-check" or "--quiet" or "-q" or "--mode" or "--keep-originals" or "--delete-source"
            or "--seven-zip" or "--no-compress" or "--no-telemetry" or "--help" or "-h" or "--version"
            or "-v" or "--";
    }

    private static int RunZstd(
        string[] zstdArgs, int level, string? dictPath, bool? checksumOverride, bool quiet, bool stdoutFlag)
    {
        if (!ZstdCli.TryParse(zstdArgs, out var job, out var parseError,
                defaultLevel: level, defaultDictPath: dictPath, defaultChecksum: checksumOverride ?? false,
                defaultQuiet: quiet, defaultStdout: stdoutFlag))
        {
            CliLog.Err($"Error: {parseError}");
            CliLog.InfoToStderr(WithProgramName(ZstdCli.UsageText));
            return ZarchiveCli.BadUsage;
        }

        // --check/--no-check are consumed by the global parser, so the
        // subcommand never sees them: apply the verb rule (checksums are a
        // compression feature) here instead of silently ignoring --check on
        // a decompression run.
        if (checksumOverride == true && job is { Compress: false, ShowHelp: false })
        {
            CliLog.Err("Error: --check is only supported when compressing (-c).");
            CliLog.InfoToStderr(WithProgramName(ZstdCli.UsageText));
            return ZarchiveCli.BadUsage;
        }

        // Binary output to stdout: informational lines must go to stderr or
        // they would corrupt piped data. Help text is never binary, so like
        // the global --help it goes to stdout.
        Action<string>? log = job!.Quiet && !job.ShowHelp
            ? null
            : job.ShowHelp
                ? Named(CliLog.Out)
                : job.OutputPath is not null
                    ? CliLog.Out
                    : CliLog.InfoToStderr;

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
                log, CliLog.Err, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            CliLog.InfoToStderr("Canceled.");
            return 130; // SIGINT shell convention, not a pack/extract code.
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int RunSeekable(
        string[] seekableArgs, int globalLevel, bool levelExplicit, string? levelOption, string? dictPath,
        bool? checksumOverride, string? checksumOption, bool quiet, bool stdoutFlag)
    {
        // Seekable frames carry no zstd dictionary (and the diff-engine
        // --patch-from has no library support), so an explicit --dict with
        // this subcommand is a usage error, not a silent ignore.
        if (dictPath != null)
        {
            CliLog.Err(
                $"Error: --dict is not supported with '{ProgramName} seekable' (seekable frames carry no dictionary).");
            return ZarchiveCli.BadUsage;
        }

        if (!SeekableCli.TryParse(seekableArgs, out var job, out var parseError,
                defaultLevel: levelExplicit ? globalLevel : 3, defaultChecksum: checksumOverride,
                defaultQuiet: quiet, defaultStdout: stdoutFlag))
        {
            CliLog.Err($"Error: {parseError}");
            CliLog.InfoToStderr(WithProgramName(SeekableCli.UsageText));
            return ZarchiveCli.BadUsage;
        }

        // The global parser consumes --check/--level/--stdout before the
        // subcommand sees them, skipping the per-verb rules SeekableCli
        // applies to explicit tokens (checksums and level are compress-only;
        // list always writes to stdout). Re-apply them here so those
        // combinations fail loud instead of being silently ignored.
        if (!job!.ShowHelp)
        {
            if (checksumOverride.HasValue && job.Command != SeekableCli.SeekableCommand.Compress)
            {
                CliLog.Err(
                    $"Error: option {checksumOption ?? "--check"} is only supported with 'seekable compress'.");
                CliLog.InfoToStderr(WithProgramName(SeekableCli.UsageText));
                return ZarchiveCli.BadUsage;
            }

            if (stdoutFlag && job.Command == SeekableCli.SeekableCommand.List)
            {
                CliLog.Err(
                    "Error: option --stdout is only supported with 'seekable compress' or 'seekable decompress' (list always writes to stdout).");
                CliLog.InfoToStderr(WithProgramName(SeekableCli.UsageText));
                return ZarchiveCli.BadUsage;
            }

            if (levelExplicit && job.Command != SeekableCli.SeekableCommand.Compress)
            {
                CliLog.Err(
                    $"Error: option {levelOption ?? "--level"} is only supported with 'seekable compress'.");
                CliLog.InfoToStderr(WithProgramName(SeekableCli.UsageText));
                return ZarchiveCli.BadUsage;
            }
        }

        // Binary output to stdout: informational lines must go to stderr or
        // they would corrupt piped data. List output IS the table, so it
        // always goes to stdout (quiet is ignored there, like the oracle).
        var parsed = job;
        var log = parsed switch
        {
            { ShowHelp: true } => Named(CliLog.Out),
            { Command: SeekableCli.SeekableCommand.List } => CliLog.Out,
            { Quiet: true } => null,
            { OutputPath: not null } => CliLog.Out,
            _ => CliLog.InfoToStderr,
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
                log, CliLog.Err, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            CliLog.InfoToStderr("Canceled.");
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
        catch (UnauthorizedAccessException ex)
        {
            CliLog.Err($"Error: cannot read dictionary file (access denied): {path}", ex);
            return null;
        }
        catch (IOException ex)
        {
            // Includes missing files/directories.
            CliLog.Err($"Error: dictionary file not found: {path}", ex);
            return null;
        }

        try
        {
            return ZstdDictionary.FromBytes(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or ZstdException)
        {
            CliLog.Err($"Error: invalid dictionary file '{path}': {ex.Message}", ex);
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
            var size = new FileInfo(isoPath).Length;
            var redumpType = XgdTables.GetRedumpIsoTypeBySize(size);
            if (redumpType < 0)
            {
                return 0;
            }

            // Wave-dependent sizes (types 5 and 7) need the PVD at 0x832D;
            // other Redump types map straight through (null stream is fine:
            // GetWave returns -1 without a stream and the switch ignores it).
            var videoType = -1;
            try
            {
                using FileStream fs = new(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
                videoType = XgdTables.GetVideoType(fs, redumpType);
            }
            catch
            {
                // ignored: fall back below, like XISOSharp.Cli.
            }

            var vType = videoType >= 0 ? videoType : 0;
            var xsType = XgdTables.GetXisoTypeFromVideo(vType);
            if (xsType < 0 || xsType >= XgdTables.XisoOffset.Length)
            {
                xsType = XgdTables.GetXgdType(redumpType);
            }

            var isoOffset = XgdTables.XisoOffset[xsType];
            if (!quiet)
            {
                var video =
 videoType >= 0 ? videoType.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown (assuming 0)";
                CliLog.Out($"Redump ISO detected (type {redumpType}, video {video}); game partition at 0x{isoOffset:X}.");
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
    /// success with <paramref name="error"/> null, else false with a reason
    /// (and the captured exception in <paramref name="errorException"/> when
    /// the failure threw).
    /// </summary>
    private static bool TryPackIso(string isoPath, string destZar, int level, bool quiet,
        IZarBlockCompressor? compressor, ZstdDictionary? dictionary, bool checksum,
        IProgress<ZarProgress>? progress, out string? error, out Exception? errorException)
    {
        error = null;
        errorException = null;
        if (!File.Exists(isoPath))
        {
            error = $"ISO file not found: {isoPath}";
            return false;
        }

#if !HAS_XISO
        error = "unavailable: this build of zar was compiled without XISOSharp support. " +
                "Rebuild with XISOSharp enabled (it is omitted only with -p:XisoSharpAvailable=false).";
        return false;
#else
        // Redump ISOs start with the video partition: the XISO game partition
        // sits at a wave-dependent offset (XISOSharp.Cli --zar resolves it the
        // same way from the same XgdTables). Packing at offset 0 would archive
        // the video area as garbage, so resolve the game offset first.
        var isoOffset = ResolveGamePartitionOffset(isoPath, quiet);

        if (!quiet) CliLog.Out($"Converting XISO to ZAR: {isoPath} -> {destZar}");

        var comp = compressor;
        if (comp == null && (level != 6 || dictionary != null || checksum))
        {
            comp = new ZstdCompressor(new ZstdCompressionOptions
            {
                Level = level,
                Dictionary = dictionary,
                ChecksumFlag = checksum,
            });
        }

        try
        {
            var ok =
 XisoZarchive.CreateZar(isoPath, destZar, isoOffset, quiet: quiet, compressor: comp, progress: progress);
            if (!quiet) CliLog.Out();
            if (ok)
            {
                if (!quiet) CliLog.Out($"Done: {destZar}");
                return true;
            }

            error = "XISO conversion failed.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            errorException = ex;
            return false;
        }
#endif
    }

    private static int PackIso(string isoPath, string? zarPath, int level, bool quiet, IZarBlockCompressor? compressor,
        ZstdDictionary? dictionary, bool checksum)
    {
        if (!File.Exists(isoPath))
        {
            CliLog.Err($"Error: ISO file not found: {isoPath}");
            return -10;
        }

#if !HAS_XISO
        CliLog.Err(
            "Error: --iso is unavailable: this build of zar was compiled without XISOSharp support.");
        CliLog.Err(
            "Rebuild with XISOSharp enabled (it is omitted only with -p:XisoSharpAvailable=false).");
        return ZarchiveCli.BadUsage;
#else
        var output = zarPath ?? DeriveZarPath(isoPath);

        if (File.Exists(output))
        {
            CliLog.Err($"Error: Output file already exists: {output}");
            return -11;
        }

        var progress = quiet ? null : new Progress<ZarProgress>(p =>
        {
            if (p.BytesTotal > 0)
            {
                var pct = (double)p.BytesCompleted / p.BytesTotal * 100;
                CliLog.Progress($"\r  {pct:F1}% ({p.BytesCompleted / (1024 * 1024)} / {p.BytesTotal / (1024 * 1024)} MiB)");
            }
        });

        if (TryPackIso(isoPath, output, level, quiet, compressor, dictionary, checksum, progress,
                out var error, out var errorException))
        {
            return 0;
        }

        CliLog.Err($"Error: {error}", errorException);
        return -13;
#endif
    }

    private static int RunBatch(string? inputPath, string? outputPath, int jobs, ZarCollisionPolicy policy,
        int level, bool quiet, IZarBlockCompressor? compressor, ZstdDictionary? dictionary, bool checksum,
        ZarProcessMode mode, bool deleteSource, string? sevenZipPath)
    {
        if (inputPath is null || !Directory.Exists(inputPath))
        {
            CliLog.Err("Error: --batch requires an input directory.");
            return ZarchiveCli.BadUsage;
        }

        var destDir = outputPath ?? inputPath;

        // A listing fault (e.g. access denied) must fail the run, not look
        // like an empty directory with exit 0.
        var listingFailed = false;
        var files = ProcessableFiles.Find(inputPath, mode, message =>
        {
            listingFailed = true;
            CliLog.Err($"Error: cannot list input directory '{inputPath}': {message}");
        });
        if (listingFailed)
        {
            return ZarchiveCli.PackFailed;
        }

        if (files.Count == 0)
        {
            if (!quiet)
            {
                CliLog.Out("No processable files found.");
            }

            return 0;
        }

        if (!quiet)
        {
            CliLog.Out($"Found {files.Count} file(s). Processing with {jobs} workers...");
        }

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
        var progress = quiet ? null : BatchProgressReporter();

        try
        {
            Directory.CreateDirectory(destDir);

            // Directories pack as-is, ISOs convert straight to .zar, archives
            // run the 7z container stage first (7z -> ISO-or-dir -> zar).
            var (directories, isos, archives) = PartitionBatchInputs(files);

            if (sevenZipPath is not null && archives.Count > 0 && !File.Exists(sevenZipPath))
            {
                // A missing tool is an environment/runtime failure, not bad
                // usage (-1 is documented as usage / invalid input only).
                CliLog.Err($"Error: 7z binary not found: {sevenZipPath}");
                return ZarchiveCli.PackFailed;
            }

            var results = new List<ZarItemResult>();
            if (directories.Count > 0)
            {
                results.AddRange(ZarPipeline.PackBatch(directories, destDir, options, progress));
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

            if (!quiet)
            {
                CliLog.Out();
            }

            var (succeeded, failed, skipped) = ReportBatchResults(results, quiet);
            var refusals = results.Count(result =>
                result.Status is not (ZarItemStatus.Completed or ZarItemStatus.Skipped) &&
                result.ErrorMessage?.StartsWith(ZarPackEngine.OutputExistsMessage, StringComparison.Ordinal) == true);

            if (!quiet)
            {
                CliLog.Out(skipped == 0
                    ? $"Batch complete: {succeeded} succeeded, {failed} failed."
                    : $"Batch complete: {succeeded} succeeded, {failed} failed, {skipped} skipped.");
            }

            // A batch whose only failures are collision refusals reports the
            // documented -11 (Refused); any other failure stays the aggregate
            // pack failure, -13.
            if (failed == 0)
            {
                return 0;
            }

            return refusals == failed ? ZarchiveCli.Refused : ZarchiveCli.PackFailed;
        }
        catch (Exception ex)
        {
            CliLog.Err($"Error: {ex.Message}", ex);
            return ZarchiveCli.PackFailed;
        }
    }

    private static (List<string> Directories, List<string> Isos, List<string> Archives) PartitionBatchInputs(
        IReadOnlyList<string> entries)
    {
        var directories = new List<string>();
        var isos = new List<string>();
        var archives = new List<string>();
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                directories.Add(entry);
            }
            else if (ProcessableFiles.IsoExtensions.Contains(Path.GetExtension(entry).ToLowerInvariant()))
            {
                isos.Add(entry);
            }
            else
            {
                archives.Add(entry);
            }
        }

        return (directories, isos, archives);
    }

    private static IProgress<ZarProgress> BatchProgressReporter()
    {
        return new Progress<ZarProgress>(p =>
        {
            if (p.Operation == ZarOperation.Pack)
            {
                CliLog.Progress(
                    $"\r  [{p.FilesCompleted}/{p.FilesTotal}] {p.Ratio * 100:F1}% {Path.GetFileName(p.SourcePath)}");
            }
        });
    }

    private static (int Succeeded, int Failed, int Skipped) ReportBatchResults(
        IReadOnlyList<ZarItemResult> results, bool quiet)
    {
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var result in results)
        {
            switch (result.Status)
            {
                case ZarItemStatus.Completed:
                    succeeded++;
                    break;
                case ZarItemStatus.Skipped:
                    skipped++;
                    if (!quiet)
                    {
                        CliLog.Out($"  Skipped: {result.SourcePath} - {result.ErrorMessage}");
                    }

                    break;
                default:
                    failed++;
                    CliLog.Err($"  Failed: {result.SourcePath} - {result.ErrorMessage}");
                    break;
            }
        }

        return (succeeded, failed, skipped);
    }

    /// <summary>
    /// Converts each ISO in <paramref name="isos"/> to a <c>.zar</c> next to
    /// it in <paramref name="destDir"/>. A single item's failure never stops
    /// the leg.
    /// </summary>
    private static IReadOnlyList<ZarItemResult> PackIsoBatch(IReadOnlyList<string> isos, string destDir,
        ZarPipelineOptions options, int level, IZarBlockCompressor? compressor, ZstdDictionary? dictionary,
        IProgress<ZarProgress>? progress)
    {
        return RunCliItems(isos.ToList(), options.MaxDegreeOfParallelism,
            iso => PackIsoOne(iso, destDir, options, level, compressor, dictionary, progress));
    }

    private static ZarItemResult PackIsoOne(string iso, string destDir, ZarPipelineOptions options,
        int level, IZarBlockCompressor? compressor, ZstdDictionary? dictionary,
        IProgress<ZarProgress>? progress)
    {
        var dest = Path.Combine(destDir, Path.GetFileName(DeriveZarPath(iso)));
        try
        {
            var resolved = ZarPackEngine.ResolveOutputPath(dest, options.CollisionPolicy);
            if (resolved is null)
            {
                return new ZarItemResult(iso, dest, ZarItemStatus.Skipped, "Output already exists.");
            }

            if (!TryPackIso(iso, resolved, level, quiet: true, compressor, dictionary, options.Checksum, progress,
                    out var error, out _))
            {
                return new ZarItemResult(iso, dest, ZarItemStatus.Failed, error);
            }

            if (options.DeleteSourceOnSuccess)
            {
                File.Delete(iso);
            }

            return new ZarItemResult(iso, resolved, ZarItemStatus.Completed);
        }
        catch (OperationCanceledException ex)
        {
            return new ZarItemResult(iso, dest, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException)
        {
            // Per-item isolation: log the item, run the rest.
            return new ZarItemResult(iso, dest, ZarItemStatus.Failed, ex.Message);
        }
    }

    // Runs one worker per item (never more than the item count) and turns
    // items with no worker slot into Cancelled results.
    private static IReadOnlyList<ZarItemResult> RunCliItems(
        IReadOnlyList<string> items, int maxWorkers, Func<string, ZarItemResult> run)
    {
        var outcomes = new ZarItemResult?[items.Count];
        Parallel.For(0, items.Count,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(Math.Max(1, maxWorkers), Math.Max(1, items.Count)),
            },
            index => outcomes[index] = run(items[index]));

        var results = new List<ZarItemResult>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            results.Add(outcomes[i] ??
                        new ZarItemResult(items[i], null, ZarItemStatus.Cancelled, "Cancelled before start."));
        }

        return results;
    }

    /// <summary>
    /// Runs the archive-container stage over <paramref name="archives"/>:
    /// each archive is extracted with 7z into a unique
    /// <c>destDir/temp_&lt;stem&gt;_&lt;id&gt;</c> (same-stem archives run
    /// concurrently, so a shared scratch path would let one worker delete the
    /// other's extraction); the first <c>.iso</c> found keeps
    /// going down the pipeline as <c>&lt;stem&gt;.iso</c>, otherwise the
    /// whole tree becomes <c>&lt;stem&gt;/</c>. Under
    /// <see cref="ZarProcessMode.ExtractArchive"/> the item stops there;
    /// under <see cref="ZarProcessMode.Auto"/> it continues to the ISO or
    /// directory leg, ending at <c>.zar</c>.
    /// </summary>
    private static IReadOnlyList<ZarItemResult> ProcessArchiveBatch(IReadOnlyList<string> archives, string destDir,
        ZarPipelineOptions options, ZarProcessMode mode, int level, bool quiet,
        IZarBlockCompressor? compressor, ZstdDictionary? dictionary, string? sevenZipPath,
        int jobs, IProgress<ZarProgress>? progress)
    {
        return RunCliItems(archives.ToList(), jobs,
            archive => ProcessArchiveOne(archive, destDir, options, mode, level,
                quiet, compressor, dictionary, sevenZipPath, progress));
    }

    private static ZarItemResult ProcessArchiveOne(string archive, string destDir, ZarPipelineOptions options,
        ZarProcessMode mode, int level, bool quiet, IZarBlockCompressor? compressor,
        ZstdDictionary? dictionary, string? sevenZipPath, IProgress<ZarProgress>? progress)
    {
        var stem = Path.GetFileNameWithoutExtension(archive);
        var scratch = Path.Combine(destDir, $"temp_{stem}_{Guid.NewGuid():N}");
        var scratchMoved = false;
        try
        {
            PrepareScratch(scratch);

            var tool = string.IsNullOrWhiteSpace(sevenZipPath) ? SevenZip.FindTool() : sevenZipPath;
            if (tool is null)
            {
                return new ZarItemResult(archive, null, ZarItemStatus.Failed,
                    "No 7z binary found. Install 7-Zip and ensure 7z is on PATH, or pass --seven-zip <path>.");
            }

            if (!quiet)
            {
                CliLog.Out($"Extracting archive: {archive}");
            }

            IProgress<double>? sevenProgress = quiet
                ? null
                : new Progress<double>(ratio => CliLog.Progress($"\r  {ratio * 100:F1}% {stem}"));
            SevenZip.Extract(archive, scratch, tool, sevenProgress, options.Pause);
            if (!quiet)
            {
                CliLog.Out();
            }

            var extracted = Directory.EnumerateFileSystemEntries(scratch, "*", SearchOption.AllDirectories).ToList();
            var iso = SevenZip.PickIsoCandidate(extracted.Where(File.Exists));

            string stagedPath;
            bool stagedIsIso;
            if (iso is not null)
            {
                var moved = ZarPackEngine.MoveIntoPlace(iso, Path.Combine(destDir, stem + ".iso"),
                    options.CollisionPolicy, isDirectory: false);
                if (moved is null)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Skipped, "Output already exists.");
                }

                stagedPath = moved;
                stagedIsIso = true;
            }
            else
            {
                if (extracted.Count == 0)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Failed, "Archive yielded no files.");
                }

                var moved = ZarPackEngine.MoveIntoPlace(scratch, Path.Combine(destDir, stem),
                    options.CollisionPolicy, isDirectory: true);
                if (moved is null)
                {
                    return new ZarItemResult(archive, null, ZarItemStatus.Skipped, "Output already exists.");
                }

                scratchMoved = true;
                stagedPath = moved;
                stagedIsIso = false;
            }

            if (mode == ZarProcessMode.ExtractArchive)
            {
                if (options.DeleteSourceOnSuccess)
                {
                    File.Delete(archive);
                }

                return new ZarItemResult(archive, stagedPath, ZarItemStatus.Completed);
            }

            var result = ContinueArchivePipeline(archive, stagedPath, stagedIsIso, destDir, options, level,
                compressor, dictionary, progress);

            // Delete the source only after the terminal stage actually
            // produced its .zar: a failed or skipped downstream stage must
            // not destroy the original archive.
            if (options.DeleteSourceOnSuccess && result.Status == ZarItemStatus.Completed)
            {
                File.Delete(archive);
            }

            return result;
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
            if (!scratchMoved)
            {
                CleanupScratch(scratch);
            }
        }
    }

    private static void PrepareScratch(string scratch)
    {
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        Directory.CreateDirectory(scratch);
    }

    private static ZarItemResult ContinueArchivePipeline(string archive, string stagedPath, bool stagedIsIso,
        string destDir, ZarPipelineOptions options, int level, IZarBlockCompressor? compressor,
        ZstdDictionary? dictionary, IProgress<ZarProgress>? progress)
    {
        // Re-stamp the source so the batch summary names the archive the user
        // asked about, not the intermediate the stage produced.
        return stagedIsIso
            ? PackIsoOne(stagedPath, destDir, options, level, compressor, dictionary, progress)
                with
                {
                    SourcePath = archive
                }
            : ZarPipeline.PackBatch([stagedPath], destDir, options, progress)[0]
                with
                {
                    SourcePath = archive
                };
    }

    private static void CleanupScratch(string scratch)
    {
        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: leftovers must never fail the item.
        }
    }

    /// <summary>
    /// Name this process was launched as: the standalone release bundles are
    /// <c>ZArchiveSharp</c> / <c>ZArchiveSharp.exe</c>, the global tool shim is
    /// <c>zar</c>. Usage and help text substitute it for the documented
    /// <c>zar</c> token so instructions always match the actual executable.
    /// </summary>
    internal static string ProgramName { get; } = ResolveProgramName();

    private static string ResolveProgramName()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(name) && !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // Launched as `dotnet ZArchiveSharp.Cli.dll`: keep the documented tool command.
        return "zar";
    }

    /// <summary>
    /// Replaces the documented <c>zar</c> command token with
    /// <see cref="ProgramName"/>. Only standalone tokens are touched, so the
    /// <c>.zar</c> file extension and words like "zstd" stay intact.
    /// </summary>
    private static string WithProgramName(string text)
    {
        var start = 0;
        var sb = new System.Text.StringBuilder(text.Length);
        while (true)
        {
            var index = text.IndexOf("zar ", start, StringComparison.Ordinal);
            if (index < 0)
            {
                sb.Append(text, start, text.Length - start);
                return sb.ToString();
            }

            var isToken = index == 0 || text[index - 1] is ' ' or '\'' or '\n' or '\r';
            if (isToken)
            {
                sb.Append(text, start, index - start).Append(ProgramName).Append(' ');
                start = index + 4;
            }
            else
            {
                // ".zar " (extension), "ZArchive..." etc.: keep as-is.
                sb.Append(text, start, index - start + 1);
                start = index + 1;
            }
        }
    }

    /// <summary>Wraps a log sink so printed usage text names the actual executable.</summary>
    private static Action<string> Named(Action<string> inner)
    {
        return message => inner(WithProgramName(message));
    }

    internal static string GetVersion()
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
        var plus = info.IndexOf('+');
        return plus >= 0 ? info[..plus] : info;
    }

    private static string DeriveZarPath(string isoPath)
    {
        var dir = Path.GetDirectoryName(isoPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(isoPath);
        if (name.EndsWith(".redump", StringComparison.OrdinalIgnoreCase))
            name = name[..^".redump".Length];
        return Path.Combine(dir, $"{name}.zar");
    }

    private static void PrintUsage()
    {
        static void Out(string text)
        {
            CliLog.Out(WithProgramName(text));
        }

        Out("Usage: zar [options] [input] [output]");
        Out("       zar zstd -c|-d [options] [input] [output]");
        Out("       zar seekable compress|decompress|list [options] [input] [output]");
        Out(string.Empty);
        Out("Pack a directory to .zar, extract a .zar, convert an XISO, or");
        Out("compress/decompress single zstd streams:");
        Out("  zar <directory> [output.zar]         Pack directory to .zar");
        Out("  zar <archive.zar> [output_dir]       Extract .zar to directory");
        Out("  zar --iso <game.iso> [output.zar]    Convert XISO to .zar");
#if HAS_XISO
        Out("                                     (Redump ISOs auto-detected: packs");
        Out("                                     the game partition, not the video)");
#else
        Out("                                     (unavailable in this build:");
        Out("                                     compiled without XISOSharp)");
#endif
        Out("  zar zstd -c [in] [out]               Compress a file/stdin to zstd");
        Out("  zar zstd -d [in] [out]               Decompress a zstd file/stdin");
        Out("  zar seekable compress [in] [out]     Compress to seekable .zst");
        Out("  zar seekable decompress [in] [out]   Decompress seekable .zst");
        Out("  zar seekable list <file>             Show seekable frame table");
        Out(string.Empty);
        Out("Options:");
        Out("  -l, --level <N>       Compression level 1-22 (default: 6)");
        Out("      --dict <file>     Dictionary file (pack/zstd; kept alongside,");
        Out("                        never stored; --no-compress ignores it)");
        Out("  -c, --stdout          Stream to stdout ('zar zstd' and 'zar seekable';");
        Out("                        inside 'zar zstd', -c means --compress instead)");
        Out("      --check           Write content checksums (pack/--iso/zstd)");
        Out("      --no-check        Do not write checksums (default; last wins)");
        Out("  -j, --jobs <N>        Parallel workers (default: 4)");
        Out("  -p, --policy <P>      Collision policy: fail, skip, overwrite, auto-rename");
        Out("                        (only with --batch; single paths always refuse)");
        Out("  -b, --batch           Batch process all files in input directory");
        Out("      --mode <M>        Batch stages (default: auto; only with --batch):");
        Out("                        auto: archives-(7z)-ISO/dir-.zar, ISOs-.zar, dirs-.zar");
        Out("                        extract-archive: extract archives with 7z, stop there");
        Out("                        extract-iso: convert ISOs to .zar;");
        Out("                        compress: pack directories to .zar");
        Out("      --seven-zip <exe> 7z binary for the archive stage (default: PATH plus");
        Out("                        the standard install location; only with --batch)");
        Out("      --keep-originals  Keep batch sources after packing (default; last wins");
        Out("                        against --delete-source; only with --batch)");
        Out("      --delete-source   Delete each batch source dir after its pack succeeds");
        Out("                        (only with --batch)");
        Out("  -o, --output <path>   Output path");
        Out("  -q, --quiet           Suppress output");
        Out("      --no-compress     Store blocks without compression");
        Out("  -v, --version         Show version");
        Out("  -h, --help            Show this help");
        Out("      --no-telemetry    Disable usage stats, update checks and bug reports");
        Out("                        (also via ZAR_BUG_REPORT=off)");
        Out(string.Empty);
        Out("Run 'zar zstd --help' for the zstd subcommand. A path literally");
        Out("named 'zstd' must be spelled ./zstd to pack/extract it.");
        Out("Run 'zar seekable --help' for the seekable subcommand (same");
        Out("./seekable escape for a colliding path).");
        Out(string.Empty);
        Out("Use '--' before paths that begin with '-'; unknown options are");
        Out("usage errors.");
    }
}