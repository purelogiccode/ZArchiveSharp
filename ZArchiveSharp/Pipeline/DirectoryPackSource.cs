namespace ZArchiveSharp.Pipeline;

/// <summary>
/// <see cref="IZarPackSource"/> over a filesystem directory. Mirrors
/// <c>ZArchiveTool.Pack</c> enumeration exactly: recursive entries, ordinal
/// sort in <c>DeterministicOrder</c> mode, <c>/</c>-separated relative paths.
/// </summary>
public sealed class DirectoryPackSource : IZarPackSource
{
    private readonly bool _deterministicOrder;

    /// <summary>Creates a source over <paramref name="sourceDirectory"/>.</summary>
    public DirectoryPackSource(string sourceDirectory, bool deterministicOrder = true)
    {
        DisplayPath = sourceDirectory;
        _deterministicOrder = deterministicOrder;
    }

    /// <inheritdoc/>
    public string DisplayPath { get; }

    /// <inheritdoc/>
    public IReadOnlyList<ZarPackEntry> Collect(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(DisplayPath))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {DisplayPath}");
        }

        var enumerated = Directory.EnumerateFileSystemEntries(DisplayPath, "*", SearchOption.AllDirectories);
        var paths = _deterministicOrder
            ? enumerated.OrderBy(p => p, StringComparer.Ordinal).ToList()
            : enumerated.ToList();

        var entries = new List<ZarPackEntry>(paths.Count);
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(DisplayPath, path).Replace('\\', '/');
            if (Directory.Exists(path))
            {
                entries.Add(new ZarPackEntry { RelativePath = relative, IsDirectory = true });
            }
            else if (File.Exists(path))
            {
                var captured = path;
                entries.Add(new ZarPackEntry
                {
                    RelativePath = relative,
                    IsDirectory = false,
                    Length = new FileInfo(captured).Length,
                    OpenRead = () => new FileStream(captured, FileMode.Open, FileAccess.Read, FileShare.Read, 65536),
                });
            }
        }

        return entries;
    }
}