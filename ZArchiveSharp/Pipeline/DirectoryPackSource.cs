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

        // Manual walk instead of SearchOption.AllDirectories: the walker must
        // not descend through directory symlinks/junctions (reparse points),
        // which could recurse forever or pack content outside the root. The
        // link itself is still collected (as an empty directory), so the tree
        // shape mirrors the source without following it.
        var paths = new List<string>();
        var pending = new Stack<string>();
        pending.Push(DisplayPath);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(path);
                if (Directory.Exists(path) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.None)
                {
                    pending.Push(path);
                }
            }
        }

        if (_deterministicOrder)
        {
            paths.Sort(StringComparer.Ordinal);
        }

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