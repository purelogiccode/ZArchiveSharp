namespace ZArchiveSharp.Pipeline;

using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

/// <summary>
/// Shared pack/extract engine behind <see cref="ZarPipeline"/>,
/// <see cref="ZArchiveTool"/> and the XISO <c>.zar</c> bridges. The loops
/// mirror <c>src/main.cpp</c> (ZArchive 0.1.2) exactly — same call sequence,
/// same refusal/delete-incomplete semantics, same error strings — while
/// adding byte/file progress, pause and cancellation.
/// </summary>
public static class ZarPackEngine
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Message used when <see cref="ZarCollisionPolicy.Fail"/> refuses an
    /// existing output. Batch callers match this prefix to tell collision
    /// refusals (exit <c>-11</c>) from other pack faults (<c>-13</c>).
    /// </summary>
    public const string OutputExistsMessage = "The output file already exists:";

    /// <summary>
    /// Resolves <paramref name="wantedPath"/> under <paramref name="policy"/>:
    /// returns the path to write, or null to skip when it already exists.
    /// </summary>
    /// <exception cref="IOException">When <see cref="ZarCollisionPolicy.Fail"/> refuses.</exception>
    public static string? ResolveOutputPath(string wantedPath, ZarCollisionPolicy policy)
    {
        if (!File.Exists(wantedPath) && !Directory.Exists(wantedPath))
        {
            return wantedPath;
        }

        return policy switch
        {
            ZarCollisionPolicy.Fail => throw new IOException($"{OutputExistsMessage} {wantedPath}"),
            ZarCollisionPolicy.Skip => null,
            ZarCollisionPolicy.Overwrite => DeleteForOverwrite(wantedPath),
            ZarCollisionPolicy.AutoRename => NextFreeSibling(wantedPath),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static string DeleteForOverwrite(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return path;
    }

    private static string NextFreeSibling(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var suffix = Path.GetExtension(path);
        for (var n = 1;; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}_{n}{suffix}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> exclusively, re-resolving on the
    /// resolve-to-create race that parallel batch items (and other processes)
    /// can lose under <see cref="ZarCollisionPolicy.AutoRename"/> or
    /// <see cref="ZarCollisionPolicy.Overwrite"/>.
    /// </summary>
    private static (FileStream Stream, string Path) CreateOutput(string path, ZarCollisionPolicy policy)
    {
        // Re-resolve from the requested path (not a failed candidate) so
        // repeated races stay canonical (out.zar, out_1.zar, ...) instead of
        // compounding suffixes (out_1_1.zar).
        var requested = path;
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536), path);
            }
            catch (IOException) when (attempt < 256 &&
                                      policy is ZarCollisionPolicy.AutoRename or ZarCollisionPolicy.Overwrite)
            {
                path = ResolveOutputPath(requested, policy) ?? throw new IOException($"{OutputExistsMessage} {path}");
            }
        }
    }

    /// <summary>
    /// Moves <paramref name="source"/> (a file or directory) to
    /// <paramref name="wantedPath"/> under <paramref name="policy"/>,
    /// re-resolving on the resolve-to-move race that parallel batch items
    /// (and other processes) can lose. Returns the path actually used, or
    /// null to skip.
    /// </summary>
    public static string? MoveIntoPlace(
        string source, string wantedPath, ZarCollisionPolicy policy, bool isDirectory)
    {
        // Re-resolve from the requested path (not a failed candidate) so
        // repeated races stay canonical (.../game, game_1, game_2) instead of
        // compounding suffixes (game_1_1).
        var requested = wantedPath;
        for (var attempt = 0;; attempt++)
        {
            var target = ResolveOutputPath(wantedPath, policy);
            if (target == null)
            {
                return null;
            }

            try
            {
                if (isDirectory)
                {
                    Directory.Move(source, target);
                }
                else
                {
                    File.Move(source, target);
                }

                return target;
            }
            catch (IOException) when (attempt < 256 &&
                                      policy is ZarCollisionPolicy.AutoRename or ZarCollisionPolicy.Overwrite)
            {
                // The free name was claimed by another worker between the
                // resolve and the move: resolve again from the request.
                wantedPath = requested;
            }
        }
    }

    /// <summary>
    /// Packs pre-collected <paramref name="entries"/> into
    /// <paramref name="zarPath"/> (must already be collision-resolved under
    /// <paramref name="collisionPolicy"/>). Returns the path actually
    /// written, which differs from <paramref name="zarPath"/> when an
    /// <see cref="ZarCollisionPolicy.AutoRename"/> race re-resolves it.
    /// </summary>
    public static string PackEntries(
        IReadOnlyList<ZarPackEntry> entries,
        string displayPath,
        string zarPath,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        ZarCollisionPolicy collisionPolicy = ZarCollisionPolicy.Fail)
    {
        options ??= new ZarPipelineOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var pause = options.Pause;
        long filesTotal = 0;
        long bytesTotal = 0;
        foreach (var e in entries)
        {
            if (!e.IsDirectory)
            {
                filesTotal++;
                bytesTotal += Math.Max(0, e.Length);
            }
        }

        var clock = Stopwatch.StartNew();
        long filesCompleted = 0;
        long bytesCompleted = 0;

        var (output, actualPath) = CreateOutput(zarPath, collisionPolicy);
        try
        {
            using (output)
            using (var writer = new ZArchiveWriter(output, options.ResolveCompressor(), options.NameOrder,
                       options.BlockWorkers(), options.ResolveCompressorFactory()))
            {
                var buffer = new byte[ZArchiveCommon.CompressedBlockSize];

                Report(string.Empty);
                foreach (var entry in entries)
                {
                    pause.WaitIfPaused(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        if (!writer.MakeDir(entry.RelativePath, recursive: false))
                        {
                            throw new InvalidOperationException($"Failed to create directory {entry.RelativePath}");
                        }

                        continue;
                    }

                    if (entry.OpenRead == null)
                    {
                        throw new ZarEntryCreateException($"Failed to create archive file {entry.RelativePath}");
                    }

                    if (!writer.StartNewFile(entry.RelativePath))
                    {
                        throw new ZarEntryCreateException($"Failed to create archive file {entry.RelativePath}");
                    }

                    using var input = OpenEntryInput(entry);
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        pause.WaitIfPaused(cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.AppendData(buffer.AsSpan(0, read));
                        bytesCompleted += read;
                        if (clock.Elapsed >= ProgressInterval)
                        {
                            Report(entry.RelativePath);
                            clock.Restart();
                        }
                    }

                    filesCompleted++;
                    Report(entry.RelativePath);
                }

                writer.Finalize();
                Report(string.Empty);
            }
        }
        catch
        {
            try
            {
                File.Delete(actualPath);
            }
            catch
            {
                /* best effort */
            }

            throw;
        }

        return actualPath;

        void Report(string current)
        {
            progress?.Report(new ZarProgress(
                ZarOperation.Pack, displayPath, actualPath, current,
                filesCompleted, filesTotal, bytesCompleted, bytesTotal));
        }
    }

    /// <summary>Opens an entry for reading, mapping I/O faults to the native <c>-15</c> fault.</summary>
    /// <exception cref="ZarInputOpenException">When the input file cannot be opened.</exception>
    private static Stream OpenEntryInput(ZarPackEntry entry)
    {
        try
        {
            return entry.OpenRead!();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ZarInputOpenException($"Failed to open input file {entry.RelativePath}", ex);
        }
    }

    private sealed record ExtractPlanEntry(
        string SrcPath,
        string RelativePath,
        bool IsDirectory,
        ulong Size,
        string LogLine);

    /// <summary>
    /// Extracts <paramref name="zarPath"/> into <paramref name="destDir"/>
    /// (created; files overwritten like <c>zarchive.exe</c>). Returns the
    /// extracted file paths relative to the archive root (<c>/</c> separated).
    /// </summary>
    /// <param name="zarPath">Archive file.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="displayPath">Label used in progress reports.</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">
    /// Optional per-entry stdout sink. When set, every visited entry is logged
    /// as <c>main.cpp</c> prints it (<c>srcPath/name</c>, i.e. a leading
    /// <c>/</c> for top-level entries), directories included, in preorder.
    /// </param>
    /// <exception cref="FileNotFoundException">When the archive is missing.</exception>
    /// <exception cref="ZarArchiveOpenException">When the archive cannot be opened (corrupt header).</exception>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static IReadOnlyList<string> ExtractEntries(
        string zarPath,
        string destDir,
        string? displayPath = null,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        if (!File.Exists(zarPath))
        {
            throw new FileNotFoundException($"Unable to find archive file: {zarPath}");
        }

        using var reader = ZArchiveReader.TryOpen(zarPath) ??
                           throw new ZarArchiveOpenException("Failed to open ZArchive.");

        return ExtractOpen(reader, zarPath, destDir, options, progress, cancellationToken, log);
    }

    /// <summary>
    /// Extracts an already-open <paramref name="reader"/> into
    /// <paramref name="destDir"/>. See <see cref="ExtractEntries"/>.
    /// </summary>
    /// <param name="reader">Open archive reader.</param>
    /// <param name="displayPath">Label used in progress reports.</param>
    /// <param name="destDir">Destination directory (created).</param>
    /// <param name="options">Extract options (pause, ...).</param>
    /// <param name="progress">Per-file/byte progress sink.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="log">Optional per-entry stdout sink (see <see cref="ExtractEntries"/>).</param>
    /// <exception cref="InvalidOperationException">On corrupt archives.</exception>
    public static IReadOnlyList<string> ExtractOpen(
        ZArchiveReader reader,
        string displayPath,
        string destDir,
        ZarPipelineOptions? options = null,
        IProgress<ZarProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        options ??= new ZarPipelineOptions();
        cancellationToken.ThrowIfCancellationRequested();
        var pause = options.Pause;
        if (options.Dictionary is not null)
        {
            // Archives packed with ZarPipelineOptions.Dictionary store
            // dictionary frames; the same dictionary must decode them (like
            // zstd -D). Inert for plain archives.
            reader.Dictionary = options.Dictionary;
        }

        var plan = new List<ExtractPlanEntry>();
        CollectEntries(reader, string.Empty, string.Empty, plan, cancellationToken);

        long filesTotal = 0;
        long bytesTotal = 0;
        foreach (var p in plan)
        {
            if (!p.IsDirectory)
            {
                filesTotal++;
                bytesTotal += (long)p.Size;
            }
        }

        Directory.CreateDirectory(destDir);
        var files = new List<string>();
        var clock = Stopwatch.StartNew();
        long filesCompleted = 0;
        long bytesCompleted = 0;
        var blockWorkers = options.BlockWorkers();

        Report(string.Empty);
        var buffer = new byte[ZArchiveCommon.CompressedBlockSize];
        foreach (var item in plan)
        {
            pause.WaitIfPaused(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Native stdout order: each entry line prints before its bytes
            // are extracted, so a mid-archive failure leaves the same prefix.
            log?.Invoke(item.LogLine);
            var outPath = Path.Combine(destDir, item.RelativePath);
            EnsureWithinRoot(destDir, outPath);
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(outPath);
                continue;
            }

            var handle = reader.LookUp(item.SrcPath);
            if (handle == ZArchiveReader.InvalidNode || !reader.IsFile(handle))
            {
                throw new InvalidOperationException($"Unable to extract file: {item.SrcPath}");
            }

            if (blockWorkers > 1 && item.Size > (ulong)ZArchiveCommon.CompressedBlockSize &&
                reader.TryGetFileRange(handle, out var fileOffset, out var fileSize) && fileSize == item.Size)
            {
                ExtractFileParallel(reader, item, outPath, fileOffset, fileSize, blockWorkers,
                    pause, cancellationToken, ref bytesCompleted, clock);
            }
            else
            {
                using var output = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                ulong offset = 0;
                while (true)
                {
                    pause.WaitIfPaused(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = reader.ReadFromFile(handle, offset, buffer);
                    if (read == 0)
                    {
                        break;
                    }

                    output.Write(buffer, 0, (int)read);
                    offset += read;
                    bytesCompleted += (long)read;
                    if (clock.Elapsed >= ProgressInterval)
                    {
                        Report(item.RelativePath);
                        clock.Restart();
                    }
                }

                if (offset != reader.GetFileSize(handle))
                {
                    throw new InvalidOperationException($"Extraction failed: {item.SrcPath}");
                }
            }

            files.Add(item.RelativePath);
            filesCompleted++;
            Report(item.RelativePath);
        }

        Report(string.Empty);
        return files;

        void Report(string current)
        {
            progress?.Report(new ZarProgress(
                ZarOperation.Extract, displayPath, destDir, current,
                filesCompleted, filesTotal, bytesCompleted, bytesTotal));
        }

        // Decodes one file's global block range in bounded parallel waves
        // and writes the waves in order: identical bytes to the sequential
        // loop above (same blocks, same order), with the same per-interval
        // progress and the same corruption contract (InvalidOperationException
        // naming the archive path; OperationCanceledException propagates raw).
        void ExtractFileParallel(
            ZArchiveReader archive, ExtractPlanEntry entry, string path,
            ulong globalOffset, ulong size, int dop,
            PauseToken gate, CancellationToken token,
            ref long completed, Stopwatch timer)
        {
            const int windowBlocks = 64;
            var waveSize = Math.Min(dop * 4, windowBlocks);
            var slots = new byte[waveSize][];
            for (var s = 0; s < waveSize; s++)
            {
                slots[s] = ArrayPool<byte>.Shared.Rent(ZArchiveCommon.CompressedBlockSize);
            }

            try
            {
                using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
                var block = globalOffset / (ulong)ZArchiveCommon.CompressedBlockSize;
                var skip = (int)(globalOffset % (ulong)ZArchiveCommon.CompressedBlockSize);
                var remaining = size;
                ulong written = 0;
                while (remaining > 0)
                {
                    gate.WaitIfPaused(token);
                    token.ThrowIfCancellationRequested();
                    var touched = ((ulong)skip + remaining + (ulong)ZArchiveCommon.CompressedBlockSize - 1) /
                                  (ulong)ZArchiveCommon.CompressedBlockSize;
                    var wave = (int)Math.Min(touched, (ulong)waveSize);
                    var waveFirst = block;
                    Exception? failure = null;
                    try
                    {
                        Parallel.For(0, wave,
                            new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = token },
                            j =>
                            {
                                if (!archive.TryDecodeBlock(waveFirst + (ulong)j, slots[j]))
                                {
                                    lock (slots)
                                    {
                                        failure ??= new InvalidOperationException($"Extraction failed: {entry.SrcPath}");
                                    }
                                }
                            });
                    }
                    catch (AggregateException ex) when (ex.InnerExceptions.Count != 0)
                    {
                        // Same unwrapped contract as the parallel pack path:
                        // callers map exact types (OCE stays raw, corruption
                        // stays InvalidOperationException). IsBatchFault only
                        // understands the inner type.
                        ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
                    }
                    if (failure is not null)
                    {
                        throw failure;
                    }

                    for (var j = 0; j < wave; j++)
                    {
                        var from = j == 0 ? skip : 0;
                        var take = (int)Math.Min(
                            (ulong)ZArchiveCommon.CompressedBlockSize - (ulong)from, remaining);
                        output.Write(slots[j], from, take);
                        remaining -= (ulong)take;
                        written += (ulong)take;
                        completed += take;
                        if (timer.Elapsed >= ProgressInterval)
                        {
                            Report(entry.RelativePath);
                            timer.Restart();
                        }
                    }

                    block += (ulong)wave;
                    skip = 0;
                }

                if (written != size)
                {
                    throw new InvalidOperationException($"Extraction failed: {entry.SrcPath}");
                }
            }
            finally
            {
                foreach (var slot in slots)
                {
                    ArrayPool<byte>.Shared.Return(slot);
                }
            }
        }
    }

    private static void CollectEntries(
        ZArchiveReader reader, string srcPath, string relPath,
        List<ExtractPlanEntry> plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dirHandle = reader.LookUp(srcPath);
        if (dirHandle == ZArchiveReader.InvalidNode || !reader.IsDirectory(dirHandle))
        {
            throw new InvalidOperationException($"Directory not found in archive: '{srcPath}'.");
        }

        var count = reader.GetDirEntryCount(dirHandle);
        for (uint i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.GetDirEntry(dirHandle, i, out var entry))
            {
                throw new InvalidOperationException("Directory contains invalid node.");
            }

            ValidateEntryName(entry.Name);
            var childSrc = string.IsNullOrEmpty(srcPath) ? entry.Name : srcPath + "/" + entry.Name;
            var childRel = string.IsNullOrEmpty(relPath) ? entry.Name : relPath + "/" + entry.Name;
            // Native stdout quirk, kept byte-identical: the root call passes
            // srcPath "" so top-level entries print with a leading "/".
            var logLine = string.IsNullOrEmpty(srcPath) ? "/" + entry.Name : childSrc;
            if (entry.IsDirectory)
            {
                plan.Add(new ExtractPlanEntry(childSrc, childRel, true, 0, logLine));
                CollectEntries(reader, childSrc, childRel, plan, cancellationToken);
            }
            else
            {
                plan.Add(new ExtractPlanEntry(childSrc, childRel, false, entry.Size, logLine));
            }
        }
    }

    // Entry names come from the archive and are used verbatim as filesystem
    // paths. A crafted archive can carry "../", absolute, drive-qualified or
    // Windows device names; extracting those escapes the destination root
    // (zip-slip). Every component must be a single, plain name.
    private static void ValidateEntryName(string name)
    {
        if (name.Length == 0 || name is "." or ".."
            || name.Contains('/') || name.Contains('\\')
            || Path.IsPathRooted(name)
            || (name.Length >= 2 && name[1] == ':' && char.IsAsciiLetter(name[0]))
            || IsReservedDeviceName(name))
        {
            throw new InvalidOperationException($"Archive entry name is not safe to extract: '{name}'.");
        }
    }

    private static bool IsReservedDeviceName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = dot < 0 ? name : name[..dot];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
               && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && stem[3] is >= '1' and <= '9';
    }

    // Defense in depth: even with validated components, the resolved path
    // must stay under the destination root before anything is created.
    private static void EnsureWithinRoot(string destDir, string outPath)
    {
        var root = Path.GetFullPath(destDir);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        var full = Path.GetFullPath(outPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison))
        {
            throw new InvalidOperationException(
                $"Archive entry escapes the destination directory: '{outPath}'.");
        }
    }
}