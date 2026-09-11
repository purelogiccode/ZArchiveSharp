namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Archive-container stage: extracts <c>.zip/.rar/.7z/.tar/.gz</c> files with
/// an external 7z binary driven through <see cref="ProcessRunner"/> (the seam
/// binary and runs the extraction; the caller decides what the extracted
/// tree means (ISO keeps going down the pipeline, anything else packs as a
/// directory).
/// </summary>
/// <remarks>
/// Two deliberate deviations from the oracle: the extract switch is
/// <c>x</c> (full paths) rather than <c>e</c> (flat) so directory trees
/// survive the stage, and the ISO search covers the whole extracted tree
/// (ordinal, case-insensitive) rather than only the top level. <c>-bsp1</c>
/// is passed exactly like the oracle so progress parses the same way.
/// </remarks>
public static class SevenZip
{
    /// <summary>Well-known Windows install locations checked after an explicit path.</summary>
    public static IReadOnlyList<string> WellKnownWindowsPaths { get; } =
    [
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
    ];

    /// <summary>Binary names probed on <c>PATH</c> (7-Zip and p7zip spellings).</summary>
    public static IReadOnlyList<string> ToolNames { get; } = ["7z", "7zz"];

    /// <summary>
    /// Finds a 7z binary: <paramref name="preferredPath"/> first (when it
    /// exists), then the well-known install locations (Windows only, unless
    /// <paramref name="probeWellKnownLocations"/> is false), then
    /// <paramref name="searchDirectories"/> (default: split of
    /// <c>PATH</c>) scanned for <see cref="ToolNames"/>. Returns the full
    /// path, or null when nothing is found.
    /// </summary>
    public static string? FindTool(
        string? preferredPath = null,
        IEnumerable<string>? searchDirectories = null,
        bool probeWellKnownLocations = true)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
        {
            return preferredPath;
        }

        if (probeWellKnownLocations && OperatingSystem.IsWindows())
        {
            foreach (var candidate in WellKnownWindowsPaths)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        var dirs = searchDirectories ??
                   (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in dirs)
        {
            string trimmed;
            try
            {
                trimmed = dir.Trim().Trim('"');
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (trimmed.Length == 0 || !Directory.Exists(trimmed))
            {
                continue;
            }

            foreach (var name in ToolNames)
            {
                var plain = Path.Combine(trimmed, name);
                if (File.Exists(plain))
                {
                    return plain;
                }

                if (OperatingSystem.IsWindows())
                {
                    var exe = plain + ".exe";
                    if (File.Exists(exe))
                    {
                        return exe;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destDir"/>
    /// (created; full paths preserved via <c>x</c>). Accepts 7z's exit 0/1
    /// like <see cref="ProcessRunner"/>; anything else throws with 7z's last
    /// line attached.
    /// </summary>
    /// <exception cref="FileNotFoundException">When no 7z binary is found.</exception>
    /// <exception cref="InvalidOperationException">When 7z reports failure.</exception>
    public static void Extract(
        string archivePath,
        string destDir,
        string? toolPath = null,
        IProgress<double>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destDir);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"Archive not found: {archivePath}");
        }

        var tool = toolPath;
        if (!string.IsNullOrWhiteSpace(tool))
        {
            if (!File.Exists(tool))
            {
                throw new FileNotFoundException($"7z binary not found: {tool}");
            }
        }
        else
        {
            tool = FindTool() ?? throw new FileNotFoundException(
                "No 7z binary found. Install 7-Zip and ensure 7z is on PATH, or pass an explicit path.");
        }

        Directory.CreateDirectory(destDir);
        ProcessRunner.Run(tool, BuildArguments(archivePath, destDir),
            workingDirectory: destDir, progress: progress, pause: pause,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Picks the pipeline input out of an extracted tree: the first
    /// <c>.iso</c> file (ordinal full-path order, extension matched
    /// case-insensitively), or null when the tree holds no ISO. When several
    /// ISOs are present the rest are ignored, like the oracle (which moves
    /// only <c>iso_files[0]</c>).
    /// </summary>
    public static string? PickIsoCandidate(IEnumerable<string> extractedFiles)
    {
        string? best = null;
        foreach (var path in extractedFiles)
        {
            if (!string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best == null || string.CompareOrdinal(path, best) < 0)
            {
                best = path;
            }
        }

        return best;
    }

    /// <summary>Builds the 7z argument line: <c>x "archive" -o"dest" -y -bsp1</c>.</summary>
    internal static string BuildArguments(string archivePath, string destDir)
    {
        return $"x \"{archivePath}\" -o\"{destDir}\" -y -bsp1";
    }
}