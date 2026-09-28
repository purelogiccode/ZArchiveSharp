namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Archive-container stage: extracts <c>.zip/.rar/.7z/.tar/.gz</c> files with
/// an external 7z binary driven through <see cref="ProcessRunner"/>. 7z stays
/// external by design — this type only locates the binary and runs the
/// extraction; the caller decides what the extracted tree means (an ISO keeps
/// going down the pipeline, anything else packs as a directory).
/// </summary>
/// <remarks>
/// The extract switch is <c>x</c> (full paths) rather than <c>e</c> (flat) so
/// directory trees survive the stage, and the ISO search covers the whole
/// extracted tree in ordinal, case-insensitive path order.
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

        if (probeWellKnownLocations && OperatingSystem.IsWindows() &&
            FirstExisting(WellKnownWindowsPaths) is { } installed)
        {
            return installed;
        }

        var directories = searchDirectories ??
                          (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var directory in directories)
        {
            var probe = CleanDirectory(directory);
            if (probe is null || !Directory.Exists(probe))
            {
                continue;
            }

            foreach (var toolName in ToolNames)
            {
                var plain = Path.Combine(probe, toolName);
                if (File.Exists(plain))
                {
                    return plain;
                }

                if (OperatingSystem.IsWindows() && File.Exists(plain + ".exe"))
                {
                    return plain + ".exe";
                }
            }
        }

        return null;

        static string? FirstExisting(IReadOnlyList<string> candidates)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                if (File.Exists(candidates[i]))
                {
                    return candidates[i];
                }
            }

            return null;
        }

        static string? CleanDirectory(string directory)
        {
            try
            {
                var trimmed = directory.Trim().Trim('"');
                return trimmed.Length == 0 ? null : trimmed;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }
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

        var tool = ResolveTool(toolPath);
        Directory.CreateDirectory(destDir);
        ProcessRunner.Run(tool, BuildArguments(archivePath, destDir),
            workingDirectory: destDir, progress: progress, pause: pause,
            cancellationToken: cancellationToken);
    }

    private static string ResolveTool(string? toolPath)
    {
        if (string.IsNullOrWhiteSpace(toolPath))
        {
            return FindTool() ?? throw new FileNotFoundException(
                "No 7z binary found. Install 7-Zip and ensure 7z is on PATH, or pass an explicit path.");
        }

        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException($"7z binary not found: {toolPath}");
        }

        return toolPath;
    }

    /// <summary>
    /// Picks the pipeline input out of an extracted tree: the first
    /// <c>.iso</c> file (ordinal full-path order, extension matched
    /// case-insensitively), or null when the tree holds no ISO. Additional
    /// ISOs are ignored.
    /// </summary>
    public static string? PickIsoCandidate(IEnumerable<string> extractedFiles)
    {
        ArgumentNullException.ThrowIfNull(extractedFiles);
        string? best = null;
        foreach (var path in extractedFiles)
        {
            if (!string.Equals(Path.GetExtension(path), ".iso", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (best is null || string.CompareOrdinal(path, best) < 0)
            {
                best = path;
            }
        }

        return best;
    }

    /// <summary>Builds the 7z argument line: <c>x "archive" -o"dest" -y -bsp1</c>.</summary>
    internal static string BuildArguments(string archivePath, string destDir)
    {
        // A quoted path ending in a separator ("C:\out\") is parsed with an
        // escaped closing quote by some 7z builds; roots ("C:\") keep theirs
        // (TrimEndingDirectorySeparator leaves roots alone).
        var output = Path.TrimEndingDirectorySeparator(destDir);
        return $"x \"{archivePath}\" -o\"{output}\" -y -bsp1";
    }
}
