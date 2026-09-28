#if NET9_0_OR_GREATER
using ProgressGate = System.Threading.Lock;
#else
using ProgressGate = object;
#endif

namespace ZArchiveSharp.Pipeline;

/// <summary>
/// High-level archive operations: single-directory pack, archive extract,
/// and parallel multi-item batches with shared progress, pause, cancellation
/// and collision handling. <see cref="ZArchiveTool"/>, the XISO <c>.zar</c>
/// bridges and the CLI all funnel through here so semantics stay uniform.
/// </summary>
public static class ZarPipeline
{
    /// <summary>
    /// Packs <paramref name="sourceDirectory"/> into a <c>.zar</c> file.
    /// Returns the archive path written (after collision resolution), or null
    /// when <see cref="ZarCollisionPolicy.Skip"/> skips an existing output.
    /// </summary>
    /// <param name="sourceDirectory">Directory to pack (recursively).</param>
    /// <param name="zarPath">
    /// Destination path, or null for <c>&lt;stem&gt;.zar</c> next to the input
    /// (the <c>zarchive.exe</c> default).
    /// </param>
    /// <param name="options">Pack options (level, policy, determinism, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static string? Pack(
        string sourceDirectory,
        string? zarPath = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ZarPipelineOptions();
        zarPath ??= DefaultZarPath(sourceDirectory);
        // Collect first, resolve the output second: an Overwrite policy must
        // not remove a previous archive when the source cannot even be read.
        var source = new DirectoryPackSource(sourceDirectory, options.DeterministicOrder);
        var entries = source.Collect(cancellationToken);
        var resolved = ZarPackEngine.ResolveOutputPath(zarPath, options.CollisionPolicy);
        if (resolved is null)
        {
            return null;
        }

        var written = ZarPackEngine.PackEntries(
            entries, sourceDirectory, resolved, options, progress, cancellationToken, options.CollisionPolicy);
        if (options.DeleteSourceOnSuccess)
        {
            Directory.Delete(sourceDirectory, recursive: true);
        }

        return written;
    }

    /// <summary>Packs an arbitrary <see cref="IZarPackSource"/> (directory tree, XISO walk, ...).</summary>
    public static void PackSource(
        IZarPackSource source,
        string zarPath,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zarPath);
        options ??= new ZarPipelineOptions();
        var entries = source.Collect(cancellationToken);
        var directory = Path.GetDirectoryName(Path.GetFullPath(zarPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var resolved = ZarPackEngine.ResolveOutputPath(zarPath, options.CollisionPolicy);
        if (resolved is null)
        {
            return;
        }

        ZarPackEngine.PackEntries(
            entries, source.DisplayPath, resolved, options, progress, cancellationToken, options.CollisionPolicy);
    }

    /// <summary>
    /// Extracts <paramref name="zarPath"/> into <paramref name="destDir"/>
    /// (created; files overwritten like <c>zarchive.exe</c>). Returns the
    /// extracted file paths relative to the archive root (<c>/</c> separated).
    /// </summary>
    /// <param name="zarPath">Archive file.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">Optional per-entry stdout sink (native <c>main.cpp</c> entry lines).</param>
    public static IReadOnlyList<string> Extract(
        string zarPath,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        return ZarPackEngine.ExtractEntries(zarPath, destDir, zarPath, options, progress, cancellationToken, log);
    }

    /// <summary>
    /// Packs several directories in parallel. Worker count is
    /// <c>min(MaxDegreeOfParallelism, items)</c>; a failing item never stops
    /// the others, and batch progress re-bases each item's ratio into its
    /// <c>1/n</c> share.
    /// </summary>
    /// <param name="sourceDirectories">Directories to pack.</param>
    /// <param name="destDir">
    /// Directory receiving the <c>.zar</c> files, or null to write each next
    /// to its source (the <c>zarchive.exe</c> default).
    /// </param>
    /// <param name="options">Pack options (level, policy, workers, ...).</param>
    /// <param name="progress">Batch progress sink (per-item ratios re-based to 1/n shares).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static IReadOnlyList<ZarItemResult> PackBatch(
        IEnumerable<string> sourceDirectories,
        string? destDir = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);
        options ??= new ZarPipelineOptions();
        var items = sourceDirectories.ToList();
        if (destDir is not null)
        {
            Directory.CreateDirectory(destDir);
        }

        return RunBatch(
            items,
            (_, item) => destDir is null ? DefaultZarPath(item) : Path.Combine(destDir, DefaultZarName(item)),
            options,
            progress,
            cancellationToken,
            (item, dest, itemProgress) =>
            {
                var written = Pack(item, dest, options, itemProgress, cancellationToken);
                return written is null
                    ? new ZarItemResult(item, dest, ZarItemStatus.Skipped, "Output already exists.")
                    : new ZarItemResult(item, written, ZarItemStatus.Completed);
            });
    }

    /// <summary>
    /// Extracts several archives in parallel. Each archive extracts into
    /// <c>destDir/&lt;stem&gt;_extracted</c> (the <c>zarchive.exe</c> default),
    /// or into <paramref name="destDir"/> itself for a single archive.
    /// Same-stem archives in one batch get unique destinations.
    /// </summary>
    public static IReadOnlyList<ZarItemResult> ExtractBatch(
        IEnumerable<string> zarPaths,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(zarPaths);
        ArgumentNullException.ThrowIfNull(destDir);
        options ??= new ZarPipelineOptions();
        var items = zarPaths.ToList();

        // a\game.zar and b\game.zar would both want destDir/game_extracted and
        // race file-by-file; hand each item its own slot within this batch.
        var destinations = new string[items.Count];
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var basis = items.Count == 1 ? destDir : Path.Combine(destDir, DefaultExtractName(items[i]));
            var choice = basis;
            for (var n = 1; !claimed.Add(choice); n++)
            {
                choice = $"{basis}_{n}";
            }

            destinations[i] = choice;
        }

        return RunBatch(
            items,
            (index, _) => destinations[index],
            options,
            progress,
            cancellationToken,
            (item, dest, itemProgress) =>
            {
                var files = Extract(item, dest, options, itemProgress, cancellationToken);
                return new ZarItemResult(item, dest, ZarItemStatus.Completed, FilesProcessed: files.Count);
            });
    }

    /// <summary>
    /// Rolls item outcomes up to one <see cref="ZarProcessState"/>: cancelled
    /// beats failed, failed beats partial, and everything-completed (or
    /// nothing at all) is completed.
    /// </summary>
    public static ZarProcessState RollUp(IEnumerable<ZarItemResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var outcomes = items.ToList();
        if (outcomes.Count == 0 || outcomes.TrueForAll(r => r.Status == ZarItemStatus.Completed))
        {
            return ZarProcessState.Completed;
        }

        if (outcomes.Exists(r => r.Status == ZarItemStatus.Cancelled))
        {
            return ZarProcessState.Cancelled;
        }

        return outcomes.Exists(r => r.Status == ZarItemStatus.Failed)
            ? ZarProcessState.Failed
            : ZarProcessState.Partial;
    }

    // Shared batch engine: maps each item to a destination, runs one worker
    // per item (never more than the item count), isolates per-item faults and
    // fills items that never started with Cancelled.
    private static IReadOnlyList<ZarItemResult> RunBatch(
        IReadOnlyList<string> items,
        Func<int, string, string> destinationAt,
        ZarPipelineOptions options,
        IProgress<ZarProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, string, IProgress<ZarProgress>?, ZarItemResult> execute)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var progressGate = new ProgressGate();
        var settled = new SettledCounter();
        var outcomes = new ZarItemResult?[items.Count];
        try
        {
            Parallel.For(0, items.Count,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.ClampedWorkers(items.Count),
                    CancellationToken = cancellationToken,
                },
                index =>
                {
                    var itemProgress = progress is null
                        ? null
                        : new BatchProgress(progress, progressGate, items.Count, settled);
                    outcomes[index] = RunItem(
                        items[index], () => destinationAt(index, items[index]), itemProgress, execute);
                    settled.MarkOne();
                });
        }
        catch (OperationCanceledException)
        {
            // Items with no worker slot stay null and turn Cancelled below.
        }

        var results = new List<ZarItemResult>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            results.Add(outcomes[i] ??
                        new ZarItemResult(items[i], null, ZarItemStatus.Cancelled, "Cancelled before start."));
        }

        return results;
    }

    private static ZarItemResult RunItem(
        string item, Func<string> destination, IProgress<ZarProgress>? itemProgress,
        Func<string, string, IProgress<ZarProgress>?, ZarItemResult> execute)
    {
        // Destination mapping is inside the guard too: a malformed source path
        // fails this item instead of escaping Parallel.For as an aggregate.
        string? resolvedDestination = null;
        try
        {
            resolvedDestination = destination();
            return execute(item, resolvedDestination, itemProgress);
        }
        catch (OperationCanceledException ex)
        {
            return new ZarItemResult(item, resolvedDestination, ZarItemStatus.Cancelled, ex.Message);
        }
        catch (Exception ex)
        {
            // Per-item isolation: the fault becomes this item's result and the
            // rest of the batch keeps running.
            return new ZarItemResult(item, resolvedDestination, ZarItemStatus.Failed, ex.Message);
        }
    }

    internal static string DefaultZarName(string sourceDirectory)
    {
        var trimmed = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFileNameWithoutExtension(Path.GetFileName(trimmed)) + ".zar";
    }

    internal static string DefaultZarPath(string sourceDirectory)
    {
        var trimmed = Path.GetFullPath(sourceDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(trimmed) ?? "";
        return Path.Combine(parent, DefaultZarName(sourceDirectory));
    }

    internal static string DefaultExtractName(string zarPath)
    {
        return Path.GetFileNameWithoutExtension(zarPath) + "_extracted";
    }

    /// <summary>
    /// Counts batch items that finished (completed, skipped, failed or
    /// cancelled). A reference type keeps the worker lambda free of a
    /// captured mutable local.
    /// </summary>
    private sealed class SettledCounter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void MarkOne()
        {
            Interlocked.Increment(ref _value);
        }
    }

    /// <summary>
    /// Re-bases a single item's counters into the batch-wide 1/n share. The
    /// settled snapshot is read under the same gate as the report so two
    /// in-flight items cannot publish a stale (lower) completion count.
    /// </summary>
    private sealed class BatchProgress(
        IProgress<ZarProgress> inner,
        ProgressGate gate,
        int itemCount,
        SettledCounter settled)
        : IProgress<ZarProgress>
    {
        private readonly ProgressGate _gate = gate;
        private readonly SettledCounter _settled = settled;
        private readonly int _itemCount = itemCount;
        private readonly IProgress<ZarProgress> _inner = inner;

        public void Report(ZarProgress value)
        {
            lock (_gate)
            {
                var done = _settled.Value;
                var itemFiles = Math.Max(1, value.FilesTotal);
                var itemBytes = Math.Max(1, value.BytesTotal);
                var filesTotal = itemFiles * _itemCount;
                var bytesTotal = itemBytes * _itemCount;
                _inner.Report(value with
                {
                    FilesCompleted = Math.Min(
                        (done * itemFiles) + Math.Max(0, value.FilesCompleted), filesTotal),
                    FilesTotal = filesTotal,
                    BytesCompleted = Math.Min(
                        (done * itemBytes) + Math.Max(0, value.BytesCompleted), bytesTotal),
                    BytesTotal = bytesTotal,
                });
            }
        }
    }
}