using ZArchiveSharp.Seekable;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Callable form of the <c>zar seekable</c> contract (seekable zstd files in
/// the zeekstd framing: independently compressed frames plus a seek table —
/// not single-frame streams, not <c>.zar</c> archives):
/// <c>zar seekable compress|decompress|list [options] [input] [output]</c>,
/// defaulting to stdin/stdout where the format allows. Cancellation
/// propagates <see cref="OperationCanceledException"/> like
/// <see cref="ZarchiveCli"/>; every other failure maps onto the existing
/// <see cref="ZarchiveCli"/> exit-code table (compress failures reuse pack
/// codes, decompress/list failures reuse extract codes).
/// </summary>
/// <remarks>
/// Deliberate deviations from the <c>zeekstd</c> 0.4.5 CLI (all documented
/// in <c>docs/cli-reference.md</c>): argument shape follows the local
/// convention (positional <c>[input] [output]</c> like <c>zar zstd</c>,
/// not <c>-o</c>); existing outputs are refused (<c>-11</c>, never an
/// interactive prompt; <c>-f/--force</c> overwrites); sizes always print raw
/// (no humanized units, no <c>-r/--raw-bytes</c>); there is no progress bar
/// (no <c>--no-progress</c>); <c>--patch-from/--patch-apply</c> are absent
/// (the library has no diff engine) as is <c>--mmap-prefix</c> (managed
/// code); levels run 1-22 like the rest of this CLI (the oracle stops at
/// 19); size suffixes accept any case (the oracle only takes exact
/// <c>B/K/kib/M/mib/G/gib</c>).
/// </remarks>
public static class SeekableCli
{
    /// <summary>Usage text printed for <c>zar seekable --help</c>.</summary>
    public const string UsageText =
        "Usage: zar seekable compress|decompress|list [options] [input] [output]\n" +
        "\n" +
        "Seekable zstd files (zeekstd-compatible framing: independently compressed\n" +
        "frames plus a seek table) — not single-frame 'zar zstd' streams:\n" +
        "  zar seekable compress [in] [out]    Compress to seekable .zst (level 3 default)\n" +
        "  zar seekable decompress [in] [out]  Decompress, whole file or a --from/--to slice\n" +
        "  zar seekable list <file>            Frame summary or per-frame detail table\n" +
        "\n" +
        "Omitted input reads stdin (compress streams it; decompress buffers it),\n" +
        "omitted output writes stdout, so pipes work:\n" +
        "  zar seekable compress big.bin | zar seekable decompress > big.back\n" +
        "\n" +
        "Compress options:\n" +
        "  -l, --level <N>       Compression level 1-22 (default: 3)\n" +
        "  -s, --frame-size <S>  New frame every S bytes: 10, 10B, 10K, 10M, 10G\n" +
        "                        (default: 2M; capped at 1G)\n" +
        "      --frame-size-policy <P>\n" +
        "                        compressed or uncompressed (default: uncompressed)\n" +
        "      --checksum        Frame checksums (default on; also --check)\n" +
        "      --no-checksum     No frame checksums (also --no-check; last flag wins)\n" +
        "      --seek-table-file <file>\n" +
        "                        Write a standalone Head table there instead of\n" +
        "                        appending the Foot table (decompress: read the\n" +
        "                        table from there instead of the file tail)\n" +
        "\n" +
        "Decompress options:\n" +
        "      --from <S>        Start at decompressed byte S (default: 0)\n" +
        "      --to <S|end>      End at decompressed byte S (default: end)\n" +
        "      --from-frame <N>  Start at frame N (not with --from)\n" +
        "      --to-frame <N|last>\n" +
        "                        End after frame N inclusive (not with --to)\n" +
        "\n" +
        "List options:\n" +
        "      --from-frame <N>  First frame shown (default: 0)\n" +
        "      --to-frame <N|last>\n" +
        "                        Last frame shown, inclusive (default: last)\n" +
        "      --num-frames <N>  Frame count shown, >= 1 (not with --to-frame)\n" +
        "  -d, --detail          Per-frame table (implied by frame bounds)\n" +
        "      --seek-table-format <F>\n" +
        "                        foot reads the appended table (default); head\n" +
        "                        means the input IS a standalone table file\n" +
        "\n" +
        "Common options (compress/decompress; list takes -q/-h only):\n" +
        "  -f, --force           Overwrite existing outputs (default: refuse, -11)\n" +
        "  -c, --stdout          Write to stdout (already the default with no output\n" +
        "                        path; an error together with one)\n" +
        "  -q, --quiet           Suppress the input -> output line (ignored by list)\n" +
        "  -h, --help            Show this help";

    /// <summary>Subcommand word after <c>seekable</c>.</summary>
    public enum SeekableCommand
    {
        /// <summary>Frame input into a seekable file.</summary>
        Compress,

        /// <summary>Decode a seekable file, whole or sliced.</summary>
        Decompress,

        /// <summary>Print the seek table.</summary>
        List,
    }

    /// <summary>Parsed <c>zar seekable</c> invocation (see <see cref="TryParse"/>).</summary>
    public sealed class SeekableJob
    {
        /// <summary>Which verb runs.</summary>
        public SeekableCommand Command { get; set; } = SeekableCommand.Compress;

        /// <summary>Input path, or null for stdin (list always needs a path).</summary>
        public string? InputPath { get; set; }

        /// <summary>Output path, or null for stdout / derived name (compress).</summary>
        public string? OutputPath { get; set; }

        /// <summary>Compression level 1-22 (compress only; default 3).</summary>
        public int Level { get; set; } = 3;

        /// <summary>Frame-size threshold in bytes (compress only; default 2 MiB).</summary>
        public int FrameSize { get; set; } = 2 * 1024 * 1024;

        /// <summary>Whether <see cref="FrameSize"/> applies to compressed or uncompressed size.</summary>
        public SeekableFrameSizePolicy Policy { get; set; } = SeekableFrameSizePolicy.Uncompressed;

        /// <summary>Write a per-frame checksum (compress only; default true, like the oracle).</summary>
        public bool Checksum { get; set; } = true;

        /// <summary>Overwrite existing outputs instead of refusing with -11.</summary>
        public bool Force { get; set; }

        /// <summary>Suppress the informational input -&gt; output line (ignored by list).</summary>
        public bool Quiet { get; set; }

        /// <summary>
        /// Standalone Head table path: compress writes it there (frames carry
        /// no Foot), decompress reads the table from there.
        /// </summary>
        public string? SeekTablePath { get; set; }

        /// <summary>Start at decompressed byte (decompress only; default 0).</summary>
        public ulong From { get; set; }

        /// <summary>End at decompressed byte, null for end (decompress only).</summary>
        public ulong? To { get; set; }

        /// <summary>Start at frame (decompress/list; not with <see cref="From"/>).</summary>
        public uint? FromFrame { get; set; }

        /// <summary>End after frame inclusive (null for last/default; not with <see cref="To"/>).</summary>
        public uint? ToFrame { get; set; }

        /// <summary>True when <c>--to-frame last</c> was given explicitly.</summary>
        public bool ToLastFrame { get; set; }

        /// <summary>Frame count to list, null when unbounded (list only; not with <see cref="ToFrame"/>).</summary>
        public uint? NumFrames { get; set; }

        /// <summary>Per-frame table instead of the summary (list only).</summary>
        public bool Detail { get; set; }

        /// <summary>Parse the input itself as a Head table (list only).</summary>
        public bool ListHeadFormat { get; set; }

        /// <summary>Print <see cref="UsageText"/> and exit 0 (from --help).</summary>
        public bool ShowHelp { get; set; }
    }

    /// <summary>
    /// Parses an oracle-style byte size: leading digits plus an optional unit
    /// suffix with optional whitespace (<c>B</c>, <c>K</c>, <c>M</c>,
    /// <c>G</c>, <c>kib</c>, <c>mib</c>, <c>gib</c>, any case — a superset of
    /// the case-sensitive oracle). Pure and total: never throws.
    /// </summary>
    /// <param name="value">Raw flag value.</param>
    /// <param name="size">Parsed byte count on success.</param>
    /// <param name="error">Human-readable reason on failure.</param>
    public static bool TryParseByteSize(string? value, out ulong size, out string? error)
    {
        size = 0;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = $"Invalid size '{value}': expected a number with optional B/K/M/G suffix (e.g. 2M).";
            return false;
        }

        int digits = 0;
        while (digits < value.Length && char.IsAsciiDigit(value[digits]))
        {
            digits++;
        }

        if (digits == 0)
        {
            error = $"Invalid size '{value}': expected a number with optional B/K/M/G suffix (e.g. 2M).";
            return false;
        }

        if (!ulong.TryParse(value.AsSpan(0, digits), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out ulong number))
        {
            error = $"Invalid size '{value}': number too large.";
            return false;
        }

        string unit = string.Concat(value.AsSpan(digits).ToString().Where(c => !char.IsWhiteSpace(c)));
        ulong factor = unit.Length == 0
            ? 1UL
            : unit.ToUpperInvariant() switch
            {
                "B" => 1UL,
                "K" or "KIB" => 1024UL,
                "M" or "MIB" => 1024UL * 1024UL,
                "G" or "GIB" => 1024UL * 1024UL * 1024UL,
                _ => 0UL,
            };

        if (factor == 0)
        {
            error = $"Invalid size '{value}': unknown unit \"{unit}\" (expected B, K, M or G).";
            return false;
        }

        try
        {
            size = checked(number * factor);
        }
        catch (OverflowException)
        {
            error = $"Invalid size '{value}': byte value too large.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Parses <paramref name="args"/> (the tokens after <c>seekable</c>) into
    /// a <see cref="SeekableJob"/>. Pure and total: never throws, reports the
    /// first problem in <paramref name="error"/> and returns false. The verb
    /// comes first (<c>compress|c|decompress|d|list|l</c>); options after it
    /// are validated per verb so a misplaced flag errors instead of being
    /// silently ignored.
    /// </summary>
    /// <param name="args">Tokens after the <c>seekable</c> word.</param>
    /// <param name="job">Parsed job on success.</param>
    /// <param name="error">Human-readable reason on failure.</param>
    /// <param name="defaultLevel">Inherited <c>--level</c> when none is given (must be 1-22).</param>
    /// <param name="defaultChecksum">Inherited checksum override, or null for the oracle default (on).</param>
    /// <param name="defaultQuiet">Inherited <c>--quiet</c>.</param>
    /// <param name="defaultStdout">Inherited <c>--stdout</c> (errors with an output path, like explicit).</param>
    public static bool TryParse(
        string[] args,
        out SeekableJob? job,
        out string? error,
        int defaultLevel = 3,
        bool? defaultChecksum = null,
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

        if (args.Length == 0)
        {
            error = "Expected compress, decompress or list (e.g. zar seekable compress in out).";
            return false;
        }

        SeekableCommand command = args[0].ToLowerInvariant() switch
        {
            "compress" or "c" => SeekableCommand.Compress,
            "decompress" or "d" => SeekableCommand.Decompress,
            "list" or "l" => SeekableCommand.List,
            _ => (SeekableCommand)(-1),
        };

        if ((int)command < 0)
        {
            if (string.Equals(args[0], "-h", StringComparison.Ordinal) ||
                string.Equals(args[0], "--help", StringComparison.Ordinal))
            {
                job = new SeekableJob { Quiet = defaultQuiet, ShowHelp = true };
                return true;
            }

            error = $"Unknown seekable command '{args[0]}': expected compress, decompress or list.";
            return false;
        }

        var parsed = new SeekableJob
        {
            Command = command,
            Level = defaultLevel,
            Checksum = defaultChecksum ?? true,
            Quiet = defaultQuiet,
        };

        bool stdout = defaultStdout;
        bool toSet = false;
        bool toFrameSet = false;
        bool numFramesSet = false;
        bool fromSet = false;
        bool fromFrameSet = false;
        var positional = new List<string>();

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-l" or "--level":
                    if (command != SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable compress'.";
                        return false;
                    }

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
                case "-s" or "--frame-size":
                    if (command != SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable compress'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for -s/--frame-size (expected like 2M).";
                        return false;
                    }

                    if (!TryParseByteSize(args[++i], out ulong frameBytes, out string? frameError))
                    {
                        error = frameError;
                        return false;
                    }

                    if (frameBytes < 1 || frameBytes > (ulong)SeekableOptions.MaxFrameSize)
                    {
                        error = $"Invalid frame size '{args[i]}': expected 1-{SeekableOptions.MaxFrameSize}.";
                        return false;
                    }

                    parsed.FrameSize = (int)frameBytes;
                    break;
                case "--frame-size-policy":
                    if (command != SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable compress'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --frame-size-policy (expected compressed or uncompressed).";
                        return false;
                    }

                    string policyRaw = args[++i];
                    if (string.Equals(policyRaw, "compressed", StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.Policy = SeekableFrameSizePolicy.Compressed;
                    }
                    else if (string.Equals(policyRaw, "uncompressed", StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.Policy = SeekableFrameSizePolicy.Uncompressed;
                    }
                    else
                    {
                        error = $"Invalid frame-size policy '{policyRaw}': expected compressed or uncompressed.";
                        return false;
                    }

                    break;
                case "--checksum" or "--check":
                    if (command != SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable compress'.";
                        return false;
                    }

                    parsed.Checksum = true;
                    break;
                case "--no-checksum" or "--no-check":
                    if (command != SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable compress'.";
                        return false;
                    }

                    parsed.Checksum = false;
                    break;
                case "--seek-table-file":
                    if (command == SeekableCommand.List)
                    {
                        error =
                            $"Option {args[i]} is only supported with 'seekable compress' or 'seekable decompress' (list takes --seek-table-format).";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --seek-table-file (expected a table path).";
                        return false;
                    }

                    parsed.SeekTablePath = args[++i];
                    break;
                case "--from":
                    if (command != SeekableCommand.Decompress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable decompress'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --from (expected a byte offset).";
                        return false;
                    }

                    if (!TryParseByteSize(args[++i], out ulong from, out string? fromError))
                    {
                        error = fromError;
                        return false;
                    }

                    parsed.From = from;
                    fromSet = true;
                    break;
                case "--to":
                    if (command != SeekableCommand.Decompress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable decompress'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --to (expected a byte offset or 'end').";
                        return false;
                    }

                    string toRaw = args[++i];
                    if (!string.Equals(toRaw, "end", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryParseByteSize(toRaw, out ulong to, out string? toError))
                        {
                            error = toError;
                            return false;
                        }

                        parsed.To = to;
                    }

                    toSet = true;
                    break;
                case "--from-frame":
                    if (command == SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable decompress' or 'seekable list'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --from-frame (expected a frame index).";
                        return false;
                    }

                    if (!uint.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture,
                            out uint fromFrame))
                    {
                        error = $"Invalid frame index '{args[i]}': expected a non-negative integer.";
                        return false;
                    }

                    parsed.FromFrame = fromFrame;
                    fromFrameSet = true;
                    break;
                case "--to-frame":
                    if (command == SeekableCommand.Compress)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable decompress' or 'seekable list'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --to-frame (expected a frame index or 'last').";
                        return false;
                    }

                    string toFrameRaw = args[++i];
                    if (string.Equals(toFrameRaw, "last", StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.ToLastFrame = true;
                    }
                    else if (uint.TryParse(toFrameRaw, System.Globalization.CultureInfo.InvariantCulture,
                                 out uint toFrame))
                    {
                        parsed.ToFrame = toFrame;
                    }
                    else
                    {
                        error = $"Invalid frame index '{toFrameRaw}': expected a non-negative integer or 'last'.";
                        return false;
                    }

                    toFrameSet = true;
                    break;
                case "--num-frames":
                    if (command != SeekableCommand.List)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable list'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --num-frames (expected a count >= 1).";
                        return false;
                    }

                    if (!uint.TryParse(args[++i], System.Globalization.CultureInfo.InvariantCulture,
                            out uint numFrames) ||
                        numFrames == 0)
                    {
                        error = $"Invalid frame count '{args[i]}': frame number must be greater than 0.";
                        return false;
                    }

                    parsed.NumFrames = numFrames;
                    numFramesSet = true;
                    break;
                case "-d" or "--detail":
                    if (command != SeekableCommand.List)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable list'.";
                        return false;
                    }

                    parsed.Detail = true;
                    break;
                case "--seek-table-format":
                    if (command != SeekableCommand.List)
                    {
                        error = $"Option {args[i]} is only supported with 'seekable list'.";
                        return false;
                    }

                    if (i + 1 >= args.Length)
                    {
                        error = "Missing value for --seek-table-format (expected foot or head).";
                        return false;
                    }

                    string formatRaw = args[++i];
                    if (string.Equals(formatRaw, "head", StringComparison.OrdinalIgnoreCase))
                    {
                        parsed.ListHeadFormat = true;
                    }
                    else if (!string.Equals(formatRaw, "foot", StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"Invalid seek-table format '{formatRaw}': expected foot or head.";
                        return false;
                    }

                    break;
                case "-f" or "--force":
                    if (command == SeekableCommand.List)
                    {
                        error =
                            $"Option {args[i]} is only supported with 'seekable compress' or 'seekable decompress' (list writes no files).";
                        return false;
                    }

                    parsed.Force = true;
                    break;
                case "-c" or "--stdout":
                    if (command == SeekableCommand.List)
                    {
                        error =
                            $"Option {args[i]} is only supported with 'seekable compress' or 'seekable decompress' (list always writes to stdout).";
                        return false;
                    }

                    stdout = true;
                    break;
                case "-q" or "--quiet":
                    parsed.Quiet = true;
                    break;
                case "-h" or "--help":
                    parsed.ShowHelp = true;
                    job = parsed;
                    return true;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        error = $"Unknown option: {args[i]}.";
                        return false;
                    }

                    positional.Add(args[i]);
                    break;
            }
        }

        if (command == SeekableCommand.Decompress)
        {
            if (fromSet && fromFrameSet)
            {
                error = "Cannot combine --from with --from-frame.";
                return false;
            }

            if (toSet && toFrameSet)
            {
                error = "Cannot combine --to with --to-frame.";
                return false;
            }
        }

        if (command == SeekableCommand.List)
        {
            if (toFrameSet && numFramesSet)
            {
                error = "Cannot combine --to-frame with --num-frames.";
                return false;
            }

            if (fromSet)
            {
                error = "Option --from is only supported with 'seekable decompress' (list takes --from-frame).";
                return false;
            }

            if (toSet)
            {
                error = "Option --to is only supported with 'seekable decompress' (list takes --to-frame).";
                return false;
            }
        }

        if (command == SeekableCommand.List && positional.Count == 0)
        {
            error = "Missing input file (usage: zar seekable list <file>).";
            return false;
        }

        if (command == SeekableCommand.List && positional.Count > 1)
        {
            error = "Too many paths (usage: zar seekable list <file>).";
            return false;
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

        if (positional.Count > 0)
        {
            parsed.InputPath = positional[0];
        }

        if (positional.Count > 1)
        {
            parsed.OutputPath = positional[1];
        }

        // Like the oracle (input name plus .zst), but only when stdout was
        // not requested: with stdin and no name there is nothing to derive.
        if (command == SeekableCommand.Compress &&
            !stdout && parsed.OutputPath is null && parsed.InputPath is not null)
        {
            parsed.OutputPath = parsed.InputPath + ".zst";
        }

        job = parsed;
        return true;
    }

    /// <summary>
    /// Runs a parsed <paramref name="job"/>: file paths open files, null paths
    /// use <paramref name="stdin"/>/<paramref name="stdout"/> (flushed, never
    /// closed). Informational lines go to <paramref name="log"/> (null
    /// silences; the list table is the output, so callers pass a real sink
    /// even when quiet), failures go to <paramref name="error"/> with a
    /// <see cref="ZarchiveCli"/> exit code. File outputs created here are
    /// deleted when the run fails, like incomplete pack outputs.
    /// </summary>
    /// <param name="job">Parsed job (see <see cref="TryParse"/>).</param>
    /// <param name="stdin">Stream for omitted input paths.</param>
    /// <param name="stdout">Stream for omitted output paths (flushed, never closed).</param>
    /// <param name="log">Informational sink, or null for quiet.</param>
    /// <param name="error">Failure sink (stderr at the process edge).</param>
    /// <param name="cancellationToken">Cancellation (propagates, never masked).</param>
    public static async Task<int> RunAsync(
        SeekableJob job,
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

        if (job.FrameSize < 1 || job.FrameSize > SeekableOptions.MaxFrameSize)
        {
            error($"Error: invalid frame size '{job.FrameSize}': expected 1-{SeekableOptions.MaxFrameSize}.");
            return ZarchiveCli.BadUsage;
        }

        return job.Command switch
        {
            SeekableCommand.Compress => await CompressAsync(job, stdin, stdout, log, error, cancellationToken)
                .ConfigureAwait(false),
            SeekableCommand.Decompress => await DecompressAsync(job, stdin, stdout, log, error, cancellationToken)
                .ConfigureAwait(false),
            SeekableCommand.List => await ListAsync(job, log, error, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(job)),
        };
    }

    private static async Task<int> CompressAsync(
        SeekableJob job, Stream stdin, Stream stdout,
        Action<string>? log, Action<string> error, CancellationToken ct)
    {
        if (!job.Quiet)
        {
            string tableSuffix = job.SeekTablePath is null ? string.Empty : $" +table {job.SeekTablePath}";
            log?.Invoke($"Seekable compress {job.InputPath ?? "stdin"} -> {job.OutputPath ?? "stdout"} " +
                        $"(level {job.Level}, {job.FrameSize} {job.Policy.ToString().ToLowerInvariant()} frames{(job.Checksum ? " +checksum" : string.Empty)}{tableSuffix})");
        }

        Stream? input = stdin;
        bool ownInput = false;
        Stream? output = stdout;
        bool ownOutput = false;
        Stream? tableOutput = null;
        bool ownTable = false;
        var created = new List<string>();
        try
        {
            if (job.InputPath is not null)
            {
                input = TryOpenInput(job.InputPath, compress: true, error, out int inputCode);
                if (input is null)
                {
                    return inputCode;
                }

                ownInput = true;
            }

            if (job.OutputPath is not null)
            {
                output = TryCreateOutput(job, job.OutputPath, compress: true, created, error, out int openCode);
                if (output is null)
                {
                    return openCode;
                }

                ownOutput = true;
            }

            if (job.SeekTablePath is not null)
            {
                tableOutput = TryCreateOutput(job, job.SeekTablePath, compress: true, created, error,
                    out int tableCode);
                if (tableOutput is null)
                {
                    return tableCode;
                }

                ownTable = true;
            }

            try
            {
                var writer = new SeekableWriter(new SeekableOptions
                {
                    Level = job.Level,
                    FrameSize = job.FrameSize,
                    Policy = job.Policy,
                    Checksum = job.Checksum,
                });

                var chunk = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    writer.Write(chunk.AsSpan(0, read));
                }

                byte[] frames;
                byte[]? table = null;
                if (tableOutput is not null)
                {
                    (frames, table) = writer.FinishHead();
                }
                else
                {
                    frames = writer.Finish();
                }

                await output.WriteAsync(frames, ct).ConfigureAwait(false);
                if (table is not null)
                {
                    await tableOutput!.WriteAsync(table, ct).ConfigureAwait(false);
                }

                // Pipes need an explicit flush: unlike file outputs, stdout is
                // never disposed here, and CopyToAsync does not flush its target.
                if (!ownOutput)
                {
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }

                if (ownTable)
                {
                    await tableOutput!.FlushAsync(ct).ConfigureAwait(false);
                }

                return ZarchiveCli.Ok;
            }
            catch (OperationCanceledException)
            {
                await DisposeOwnedAsync([(input, ownInput), (output, ownOutput), (tableOutput, ownTable)])
                    .ConfigureAwait(false);
                DeleteCreated(created);
                throw;
            }
            catch (Exception ex) when (ex is ZstdException or IOException or UnauthorizedAccessException)
            {
                error($"Error: compression failed: {ex.Message}");
                await DisposeOwnedAsync([(input, ownInput), (output, ownOutput), (tableOutput, ownTable)])
                    .ConfigureAwait(false);
                DeleteCreated(created);
                return ZarchiveCli.PackFailed;
            }
        }
        finally
        {
            await DisposeOwnedAsync([(input, ownInput), (output, ownOutput), (tableOutput, ownTable)])
                .ConfigureAwait(false);
        }
    }

    private static async Task<int> DecompressAsync(
        SeekableJob job, Stream stdin, Stream stdout,
        Action<string>? log, Action<string> error, CancellationToken ct)
    {
        byte[] frames;
        if (job.InputPath is not null)
        {
            try
            {
                frames = await File.ReadAllBytesAsync(job.InputPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                error($"Error: input file not found: {job.InputPath}");
                return ZarchiveCli.NotFound;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error($"Error: cannot read input file: {job.InputPath}");
                return ZarchiveCli.ExtractionFailed;
            }
        }
        else
        {
            // The Foot table lives at the end of the file, so stdin must
            // be buffered before the table can be parsed.
            await using var buffered = new MemoryStream();
            try
            {
                await stdin.CopyToAsync(buffered, 65536, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error($"Error: cannot read stdin: {ex.Message}");
                return ZarchiveCli.ExtractionFailed;
            }

            frames = buffered.ToArray();
        }

        SeekTable table;
        if (job.SeekTablePath is not null)
        {
            byte[] tableBytes;
            try
            {
                tableBytes = await File.ReadAllBytesAsync(job.SeekTablePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                error($"Error: seek table file not found: {job.SeekTablePath}");
                return ZarchiveCli.NotFound;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error($"Error: cannot read seek table file: {job.SeekTablePath}");
                return ZarchiveCli.ExtractionFailed;
            }

            try
            {
                table = SeekTable.ParseHead(tableBytes);
            }
            catch (ZstdException ex)
            {
                error($"Error: invalid seek table '{job.SeekTablePath}': {ex.Message}");
                return ZarchiveCli.ExtractionFailed;
            }
        }
        else
        {
            try
            {
                table = SeekTable.ParseFoot(frames);
            }
            catch (ZstdException ex)
            {
                error($"Error: no seek table in input: {ex.Message}");
                return ZarchiveCli.ExtractionFailed;
            }
        }

        SeekableReader reader;
        try
        {
            reader = new SeekableReader(frames, table);
        }
        catch (ZstdException ex)
        {
            error($"Error: invalid seekable file: {ex.Message}");
            return ZarchiveCli.ExtractionFailed;
        }

        if (table.FrameCount == 0)
        {
            error("Error: seek table has no frames.");
            return ZarchiveCli.ExtractionFailed;
        }

        ulong offset;
        ulong limit;
        try
        {
            offset = job.FromFrame.HasValue
                ? table.FrameStartDecomp(checked((int)job.FromFrame.Value))
                : job.From;
            limit = job.ToLastFrame
                ? table.FrameEndDecomp(table.FrameCount - 1)
                : job.ToFrame.HasValue
                    ? table.FrameEndDecomp(checked((int)job.ToFrame.Value))
                    : job.To ?? table.TotalDecomp;
        }
        catch (ZstdException ex)
        {
            error($"Error: frame out of range: {ex.Message}");
            return ZarchiveCli.ExtractionFailed;
        }
        catch (OverflowException)
        {
            error("Error: frame index too large.");
            return ZarchiveCli.ExtractionFailed;
        }

        if (offset > limit)
        {
            error($"Error: invalid range: start ({offset}) is past end ({limit}).");
            return ZarchiveCli.BadUsage;
        }

        byte[] decoded;
        try
        {
            decoded = reader.DecompressRange(checked((long)offset), checked((long)(limit - offset)));
        }
        catch (ZstdException ex)
        {
            error($"Error: decompression failed: {ex.Message}");
            return ZarchiveCli.ExtractionFailed;
        }
        catch (OverflowException)
        {
            error("Error: range too large to decode.");
            return ZarchiveCli.ExtractionFailed;
        }

        bool partial = offset != 0 || limit != table.TotalDecomp;
        if (!job.Quiet)
        {
            string rangeSuffix = partial ? $" (bytes {offset}-{limit} of {table.TotalDecomp})" : string.Empty;
            log?.Invoke($"Seekable decompress {job.InputPath ?? "stdin"} -> {job.OutputPath ?? "stdout"}{rangeSuffix}");
        }

        Stream? output = stdout;
        bool ownOutput = false;
        var created = new List<string>();
        try
        {
            if (job.OutputPath is not null)
            {
                output = TryCreateOutput(job, job.OutputPath, compress: false, created, error, out int openCode);
                if (output is null)
                {
                    return openCode;
                }

                ownOutput = true;
            }

            try
            {
                await output.WriteAsync(decoded, ct).ConfigureAwait(false);
                if (!ownOutput)
                {
                    await output.FlushAsync(ct).ConfigureAwait(false);
                }

                return ZarchiveCli.Ok;
            }
            catch (OperationCanceledException)
            {
                await DisposeOwnedAsync([(output, ownOutput)]).ConfigureAwait(false);
                DeleteCreated(created);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error($"Error: cannot write output: {ex.Message}");
                await DisposeOwnedAsync([(output, ownOutput)]).ConfigureAwait(false);
                DeleteCreated(created);
                return ZarchiveCli.ExtractionFailed;
            }
        }
        finally
        {
            await DisposeOwnedAsync([(output, ownOutput)]).ConfigureAwait(false);
        }
    }

    private static async Task<int> ListAsync(
        SeekableJob job, Action<string>? log, Action<string> error, CancellationToken ct)
    {
        if (job.InputPath is null)
        {
            error("Error: missing input file (usage: zar seekable list <file>).");
            return ZarchiveCli.BadUsage;
        }

        byte[] fileBytes;
        try
        {
            fileBytes = await File.ReadAllBytesAsync(job.InputPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error($"Error: input file not found: {job.InputPath}");
            return ZarchiveCli.NotFound;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: cannot read input file: {job.InputPath}");
            return ZarchiveCli.ExtractionFailed;
        }

        SeekTable table;
        try
        {
            table = job.ListHeadFormat ? SeekTable.ParseHead(fileBytes) : SeekTable.ParseFoot(fileBytes);
        }
        catch (ZstdException ex)
        {
            error($"Error: invalid seek table: {ex.Message}");
            return ZarchiveCli.ExtractionFailed;
        }

        if (table.FrameCount == 0)
        {
            error("Error: seek table has no frames.");
            return ZarchiveCli.ExtractionFailed;
        }

        int start = job.FromFrame.HasValue ? checked((int)job.FromFrame.Value) : 0;
        int end;
        bool bounded = job.ToFrame.HasValue || job.ToLastFrame || job.NumFrames.HasValue;
        if (job.NumFrames.HasValue)
        {
            long wide = (long)start + (job.NumFrames.Value - 1);
            if (wide > int.MaxValue)
            {
                error("Error: frame range too large.");
                return ZarchiveCli.ExtractionFailed;
            }

            end = (int)wide;
        }
        else if (job.ToLastFrame || !job.ToFrame.HasValue)
        {
            end = table.FrameCount - 1;
        }
        else
        {
            end = checked((int)job.ToFrame!.Value);
        }

        if (start > end)
        {
            error($"Error: start frame ({start}) cannot be greater than end frame ({end}).");
            return ZarchiveCli.BadUsage;
        }

        if (start >= table.FrameCount || end >= table.FrameCount)
        {
            error($"Error: frame range {start}-{end} out of range (0-{table.FrameCount - 1}).");
            return ZarchiveCli.ExtractionFailed;
        }

        // Quiet is ignored in list mode, like the oracle: the table is the output.
        if (!job.Detail && !job.FromFrame.HasValue && !bounded)
        {
            double ratio = table.TotalComp == 0 ? 0 : table.TotalDecomp / (double)table.TotalComp;
            log?.Invoke(
                $"{"Frames",-15} {"Compressed",-15} {"Uncompressed",-15} {"Max Frame Size",-15} {"Ratio",-10} {"Filename",-15}");
            log?.Invoke(
                $"{table.FrameCount,-15} {table.TotalComp,-15} {table.TotalDecomp,-15} {table.MaxFrameSizeDecomp(),-15} {ratio.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),-10} {job.InputPath,-15}");
            return ZarchiveCli.Ok;
        }

        log?.Invoke(
            $"{"Frame Index",-15} {"Compressed",-15} {"Uncompressed",-15} {"Compressed Offset",-20} {"Uncompressed Offset",-20}");
        for (int n = start; n <= end; n++)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke(
                $"{n,-15} {table.FrameSizeComp(n),-15} {table.FrameSizeDecomp(n),-15} {table.FrameStartComp(n),-20} {table.FrameStartDecomp(n),-20}");
        }

        return ZarchiveCli.Ok;
    }

    private static Stream? TryOpenInput(string path, bool compress, Action<string> error, out int code)
    {
        try
        {
            code = ZarchiveCli.Ok;
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            error(compress
                ? $"Error: cannot open input file: {path}"
                : $"Error: input file not found: {path}");
            code = compress ? ZarchiveCli.InputNotReadable : ZarchiveCli.NotFound;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: cannot open input file: {path}");
            code = compress ? ZarchiveCli.InputNotReadable : ZarchiveCli.ExtractionFailed;
            return null;
        }
    }

    /// <summary>
    /// Creates the job output (refusing to clobber unless
    /// <see cref="SeekableJob.Force"/>). Records created paths in
    /// <paramref name="created"/> for failure cleanup. Returns the refusal
    /// code (-11) or the creation-failure code (-16 compress / -12
    /// decompress) with <c>null</c>.
    /// </summary>
    private static Stream? TryCreateOutput(
        SeekableJob job, string path, bool compress, List<string> created, Action<string> error, out int code)
    {
        if (!job.Force && (File.Exists(path) || Directory.Exists(path)))
        {
            error($"Error: output file already exists: {path}");
            code = ZarchiveCli.Refused;
            return null;
        }

        try
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, false);
            created.Add(path);
            code = ZarchiveCli.Ok;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error($"Error: cannot create output file: {path}");
            code = compress ? ZarchiveCli.PackOutputFailed : ZarchiveCli.ExtractionFailed;
            return null;
        }
    }

    private static async ValueTask DisposeOwnedAsync((Stream? Stream, bool Owned)[] owned)
    {
        foreach (var (stream, ownedFlag) in owned)
        {
            if (stream is not null && ownedFlag)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static void DeleteCreated(List<string> created)
    {
        foreach (var path in created)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: the failure code already reports the real problem.
            }
        }
    }
}