namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Batch file listing: a non-recursive scan of a directory, lowercase-suffix
/// matching per <see cref="ZarProcessMode"/>, ordinal sort; unreadable
/// entries are skipped and a failed listing is reported through the optional
/// log sink.
/// </summary>
public static class ProcessableFiles
{
    /// <summary>Archive suffixes handled by the archive stage.</summary>
    public static IReadOnlySet<string> ArchiveExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ".zip", ".rar", ".7z", ".tar", ".gz" };

    /// <summary>Disc-image suffixes handled by the XISO stage.</summary>
    public static IReadOnlySet<string> IsoExtensions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { ".iso" };

    /// <summary>
    /// Lists processable entries of <paramref name="directory"/> for
    /// <paramref name="mode"/> (<c>Auto</c> accepts archives, ISOs and whole
    /// directories). Returns full paths, sorted ordinally.
    /// </summary>
    public static IReadOnlyList<string> Find(string directory, ZarProcessMode mode, Action<string>? log = null)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(ex.Message);
            return [];
        }

        var found = new List<string>();
        foreach (var entry in entries)
        {
            if (!TryClassify(entry, out var isFile, out var isDirectory))
            {
                continue;
            }

            if (Accepts(mode, Path.GetExtension(entry).ToLowerInvariant(), isFile, isDirectory))
            {
                found.Add(entry);
            }
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static bool TryClassify(string entry, out bool isFile, out bool isDirectory)
    {
        try
        {
            isFile = File.Exists(entry);
            isDirectory = !isFile && Directory.Exists(entry);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            isFile = false;
            isDirectory = false;
            return false;
        }
    }

    private static bool Accepts(ZarProcessMode mode, string suffix, bool isFile, bool isDirectory)
    {
        return mode switch
        {
            ZarProcessMode.Auto =>
                isDirectory || (isFile && (IsoExtensions.Contains(suffix) || ArchiveExtensions.Contains(suffix))),
            ZarProcessMode.ExtractArchive => isFile && ArchiveExtensions.Contains(suffix),
            ZarProcessMode.ExtractIso => isFile && IsoExtensions.Contains(suffix),
            ZarProcessMode.Compress => isDirectory,
            _ => false,
        };
    }
}
