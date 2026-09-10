using ZARSharp.Zstd;

namespace ZARSharp.Pipeline;

/// <summary>
/// Callable form of the <c>zar zstd</c> contract (single zstd files, not
/// archives): <c>zar zstd -c|-d [-l N] [--dict f] [--check/--no-check] [in]
/// [out]</c>, defaulting to stdin/stdout when paths are omitted.
/// Cancellation propagates <see cref="OperationCanceledException"/> like
/// <see cref="ZarchiveCli"/>; every other failure maps onto the existing
/// <see cref="ZarchiveCli"/> exit-code table (no new codes: compress failures
/// reuse pack codes, decompress failures reuse extract codes).
/// </summary>
public static class ZstdCli
{
    /// <summary>Usage text printed for <c>zar zstd --help</c>.</summary>
    public const string UsageText =
        "Usage: zar zstd -c|-d [-l N] [--dict <file>] [--check|--no-check] [-q] [input] [output]\n" +
        "\n" +
        "Compress or decompress a single zstd stream (not a .zar archive):\n" +
        "  zar zstd -c [in] [out]   Compress (level 6 default)\n" +
        "  zar zstd -d [in] [out]   Decompress\n" +
        "\n" +
        "Omitted input reads stdin, omitted output writes stdout, so pipes work:\n" +
        "  zar zstd -c big.bin | zar zstd -d > big.back\n" +
        "\n" +
        "Options:\n" +
        "  -c, --compress        Compress (exactly one of -c/-d is required)\n" +
        "  -d, --decompress      Decompress\n" +
        "  -l, --level <N>       Compression level 1-22, compress only (default: 6)\n" +
        "      --dict <file>     Dictionary file (zstd -D semantics: never stored,\n" +
        "                        keep it alongside; required on both sides for dict frames)\n" +
        "      --stdout          Explicit stdout (already the default when no output\n" +
        "                        path is given; an error together with one)\n" +
        "      --check           Write a content checksum (compress only)\n" +
        "      --no-check        Do not write a checksum (default; last flag wins)\n" +
        "  -q, --quiet           Suppress the input -> output line\n" +
        "  -h, --help            Show this help\n" +
        "\n" +
        "Note: inside 'zar zstd', -c means --compress (not --stdout).";

    /// <summary>Parsed <c>zar zstd</c> invocation (see <see cref="TryParse"/>).</summary>
    public sealed class ZstdJob
    {
        /// <summary>True for <c>-c</c> (compress), false for <c>-d</c> (decompress).</summary>
        public bool Compress { get; set; } = true;

        /// <summary>Input path, or null for stdin.</summary>
        public string? InputPath { get; set; }

        /// <summary>Output path, or null for stdout.</summary>
        public string? OutputPath { get; set; }

        /// <summary>Compression level 1-22 (compress only; default 6).</summary>
        public int Level { get; set; } = 6;

        /// <summary>Dictionary file path, or null for plain frames.</summary>
        public string? DictPath { get; set; }

        /// <summary>Write a content checksum (compress only; default false).</summary>
        public bool Checksum { get; set; }

        /// <summary>Suppress the informational input -&gt; output line.</summary>
        public bool Quiet { get; set; }

        /// <summary>Print <see cref="UsageText"/> and exit 0 (from --help).</summary>
        public bool ShowHelp { get; set; }
    }

    /// <summary>
    /// Parses <paramref name="args"/> (the tokens after <c>zstd</c>) into a
    /// <see cref="ZstdJob"/>. Pure and total: never throws, reports the first
    /// problem in <paramref name="error"/> and returns false.
    /// </summary>
    /// <param name="args">Tokens after the <c>zstd</c> word.</param>
    /// <param name="job">Parsed job on success.</param>
    /// <param name="error">Human-readable reason on failure.</param>
    /// <param name="defaultLevel">Inherited <c>--level</c> when none is given (must be 1-22).</param>
    /// <param name="defaultDictPath">Inherited <c>--dict</c> when none is given.</param>
    /// <param name="defaultChecksum">Inherited checksum override.</param>
    /// <param name="defaultQuiet">Inherited <c>--quiet</c>.</param>
    /// <param name="defaultStdout">Inherited <c>--stdout</c> (errors with an output path, like explicit).</param>
    public static bool TryParse(
        string[] args,
        out ZstdJob? job,
        out string? error,
        int defaultLevel = 6,
        string? defaultDictPath = null,
        bool defaultChecksum = false,
        bool defaultQuiet = false,
        bool defaultStdout = false)
    {
        ArgumentNullException.ThrowIfNull(args);
        job = null;
        error = null;

        if (defaultLevel < 1 || defaultLevel > 22)
        {
            error = $"Invalid level '{defaultLevel}': expected 1-22.";
            return false;
        }

        var parsed = new ZstdJob
        {
            Level = defaultLevel,
            DictPath = defaultDictPath,
            Checksum = defaultChecksum,
            Quiet = defaultQuiet,
        };

        bool? mode = null; // true = compress, false = decompress
        bool stdout = defaultStdout;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-c" or "--compress":
                    if (mode == false)
                    {
                        error = "Cannot combine -c/--compress with -d/--decompress.";
                        return false;
                    }

                    mode = true;
                    break;
                case "-d" or "--decompress":
                    if (mode == true)
                    {
                        error = "Cannot combine -c/--compress with -d/--decompress.";
                        return false;
                    }

                    mode = false;
                    break;
                case "-l" or "--level":
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for -l/--level (expected 1-22).";
                        return false;
                    }

                    if (!int.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture, out int lv) ||
                        lv < 1 || lv > 22)
                    {
                        error = $"Invalid level '{args[i]}': expected 1-22.";
                        return false;
                    }

                    parsed.Level = lv;
                    break;
                case "--dict":
                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --dict (expected a dictionary file).";
                        return false;
                    }

                    parsed.DictPath = args[++i];
                    break;
                case "--stdout":
                    stdout = true;
                    break;
                case "--check":
                    parsed.Checksum = true;
                    break;
                case "--no-check":
                    parsed.Checksum = false;
                    break;
                case "-q" or "--quiet":
                    parsed.Quiet = true;
                    break;
                case "-h" or "--help":
                    parsed.ShowHelp = true;
                    job = parsed;
                    return true;
                default:
                    if (args[i].StartsWith("-", StringComparison.Ordinal))
                    {
                        error = $"Unknown option: {args[i]}.";
                        return false;
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count > 2)
        {
            error = "Too many paths (expected at most [input] [output]).";
            return false;
        }

        if (stdout && positional.Count > 1)
        {
            error = "Cannot combine --stdout with an output path.";
            return false;
        }

        if (mode is null)
        {
            error = "One of -c/--compress or -d/--decompress is required.";
            return false;
        }

        parsed.Compress = mode.Value;
        if (positional.Count > 0)
        {
            parsed.InputPath = positional[0];
        }

        if (positional.Count > 1)
        {
            parsed.OutputPath = positional[1];
        }

        job = parsed;
        return true;
    }

    /// <summary>
    /// Runs a parsed <paramref name="job"/>: file paths open files, null paths
    /// use <paramref name="stdin"/>/<paramref name="stdout"/> (flushed, never
    /// closed). Informational lines go to <paramref name="log"/> (null
    /// silences), failures go to <paramref name="error"/> with a
    /// <see cref="ZarchiveCli"/> exit code. A file output created here is
    /// deleted when the run fails, like incomplete pack outputs.
    /// </summary>
    /// <param name="job">Parsed job (see <see cref="TryParse"/>).</param>
    /// <param name="stdin">Stream for omitted input paths.</param>
    /// <param name="stdout">Stream for omitted output paths (flushed, never closed).</param>
    /// <param name="log">Informational sink, or null for quiet.</param>
    /// <param name="error">Failure sink (stderr at the process edge).</param>
    /// <param name="cancellationToken">Cancellation (propagates, never masked).</param>
    public static async Task<int> RunAsync(
        ZstdJob job,
        Stream stdin,
        Stream stdout,
        Action<string>? log,
        Action<string> error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(error);

        if (job.ShowHelp)
        {
            log?.Invoke(UsageText);
            return ZarchiveCli.Ok;
        }

        if (job.Level < 1 || job.Level > 22)
        {
            error($"Error: invalid level '{job.Level}': expected 1-22.");
            return ZarchiveCli.BadUsage;
        }

        ZstdDictionary? dict = null;
        if (job.DictPath is not null)
        {
            dict = await LoadDictionaryAsync(job.DictPath, error, cancellationToken).ConfigureAwait(false);
            if (dict is null)
            {
                return ZarchiveCli.BadUsage;
            }
        }

        if (!job.Quiet)
        {
            string dictSuffix = dict is null ? string.Empty : $" (dict 0x{dict.DictId:X8})";
            log?.Invoke(job.Compress
                ? $"Compressing {job.InputPath ?? "stdin"} -> {job.OutputPath ?? "stdout"} (level {job.Level}{dictSuffix}{(job.Checksum ? " +checksum" : string.Empty)})"
                : $"Decompressing {job.InputPath ?? "stdin"} -> {job.OutputPath ?? "stdout"}{dictSuffix}");
        }

        Stream? input = stdin;
        Stream? output = stdout;
        bool ownInput = false;
        bool ownOutput = false;
        try
        {
            if (job.InputPath is not null)
            {
                input = TryOpenInput(job, job.InputPath, error, out int inputCode);
                if (input is null)
                {
                    return inputCode;
                }

                ownInput = true;
            }

            if (job.OutputPath is not null)
            {
                int openCode = TryOpenOutput(job, job.OutputPath, error, out output);
                if (output is null)
                {
                    return openCode;
                }

                ownOutput = true;
            }

            try
            {
                if (job.Compress)
                {
                    var options = new ZstdCompressionOptions
                    {
                        Level = job.Level,
                        ChecksumFlag = job.Checksum,
                        Dictionary = dict,
                    };
                    await using var compressor = new ZstdCompressionStream(output, options, leaveOpen: true);
                    await input.CopyToAsync(compressor, 65536, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await using var decompressor =
                        new ZstdDecompressionStream(input, ZstdDecoderOptions.Default, dict, leaveOpen: true);
                    await decompressor.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                }

                // Pipes need an explicit flush: unlike file outputs, stdout is
                // never disposed here, and CopyToAsync does not flush its target.
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                return ZarchiveCli.Ok;
            }
            catch (OperationCanceledException)
            {
                await DisposeOwnedAsync(input, output, ownInput, ownOutput).ConfigureAwait(false);
                DeletePartialOutput(job);
                throw;
            }
            catch (Exception ex) when (ex is ZstdException or IOException or UnauthorizedAccessException)
            {
                error(job.Compress
                    ? $"Error: compression failed: {ex.Message}"
                    : $"Error: decompression failed: {ex.Message}");
                await DisposeOwnedAsync(input, output, ownInput, ownOutput).ConfigureAwait(false);
                DeletePartialOutput(job);
                return job.Compress ? ZarchiveCli.PackFailed : ZarchiveCli.ExtractionFailed;
            }
        }
        finally
        {
            await DisposeOwnedAsync(input, output, ownInput, ownOutput).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads a dictionary file (null on failure after reporting to
    /// <paramref name="error"/>). Missing/unreadable/invalid dictionaries are
    /// argument errors (<see cref="ZarchiveCli.BadUsage"/> at the call site):
    /// they are detected before any stream work starts.
    /// </summary>
    private static async Task<ZstdDictionary?> LoadDictionaryAsync(
        string path, Action<string> error, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: dictionary file not found: {path}");
            return null;
        }

        try
        {
            return ZstdDictionary.FromBytes(bytes);
        }
        catch (Exception ex) when (ex is ArgumentException or ZstdException)
        {
            error($"Error: invalid dictionary file '{path}': {ex.Message}");
            return null;
        }
    }

    private static Stream? TryOpenInput(ZstdJob job, string path, Action<string> error, out int code)
    {
        try
        {
            code = ZarchiveCli.Ok;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error(job.Compress
                ? $"Error: cannot open input file: {path}"
                : $"Error: input file not found: {path}");
            code = job.Compress ? ZarchiveCli.InputNotReadable : ZarchiveCli.NotFound;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: cannot open input file: {path}");
            code = job.Compress ? ZarchiveCli.InputNotReadable : ZarchiveCli.ExtractionFailed;
            return null;
        }
    }

    /// <summary>
    /// Opens the job output (refusing to clobber). Returns the refusal code
    /// (-11) or the creation-failure code (-16 pack / -12 extract) with
    /// <paramref name="output"/> null.
    /// </summary>
    private static int TryOpenOutput(ZstdJob job, string path, Action<string> error, out Stream? output)
    {
        output = null;
        if (File.Exists(path) || Directory.Exists(path))
        {
            error($"Error: output file already exists: {path}");
            return ZarchiveCli.Refused;
        }

        try
        {
            output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, false);
            return ZarchiveCli.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: cannot create output file: {path}");
            return job.Compress ? ZarchiveCli.PackOutputFailed : ZarchiveCli.ExtractionFailed;
        }
    }

    private static async ValueTask DisposeOwnedAsync(Stream? input, Stream? output, bool ownInput, bool ownOutput)
    {
        if (input is not null && ownInput)
        {
            await input.DisposeAsync().ConfigureAwait(false);
        }

        if (output is not null && ownOutput)
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static void DeletePartialOutput(ZstdJob job)
    {
        if (job.OutputPath is null)
        {
            return;
        }

        try
        {
            File.Delete(job.OutputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the failure code already reports the real problem.
        }
    }
}
