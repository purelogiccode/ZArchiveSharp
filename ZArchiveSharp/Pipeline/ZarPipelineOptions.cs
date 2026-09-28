using ZArchiveSharp.Zstd;

namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Options for <see cref="ZarPipeline"/> pack/extract work. Defaults keep
/// <c>zarchive.exe</c> parity for the codec (zstd level 6, no block checksums)
/// and use 4 workers for batch/block parallelism.
/// </summary>
public sealed class ZarPipelineOptions
{
    /// <summary>zstd level 1..22 for packing (default 6). Ignored for extract.</summary>
    public int Level { get; set; } = 6;

    /// <summary>Write per-block content checksums (default false, upstream parity).</summary>
    public bool Checksum { get; set; }

    /// <summary>
    /// Dictionary for pack block compression and archive extraction (default
    /// null = plain frames). When set, pack writes dictionary frames for the
    /// blocks where the dictionary pays, and extract needs the same dictionary
    /// for those blocks — like <c>zstd -D</c>, the dictionary file itself is
    /// never stored in the archive, so keep it alongside. A dictionary is
    /// inert for plain frames. Ignored when <see cref="Compressor"/> is set.
    /// </summary>
    public ZstdDictionary? Dictionary { get; set; }

    /// <summary>
    /// Explicit block compressor, or null to build one from
    /// <see cref="Level"/>/<see cref="Checksum"/>. Pass
    /// <c>new ZarRawCompressor()</c> to store blocks raw.
    /// </summary>
    public IZarBlockCompressor? Compressor { get; set; }

    /// <summary>
    /// Sort directory entries ordinally before packing (default true).
    /// This is <see cref="ZArchiveTool"/>'s reproducible mode; false keeps
    /// native enumeration order like <c>recursive_directory_iterator</c>.
    /// </summary>
    public bool DeterministicOrder { get; set; } = true;

    /// <summary>What to do when the output path already exists (default Fail).</summary>
    public ZarCollisionPolicy CollisionPolicy { get; set; } = ZarCollisionPolicy.Fail;

    /// <summary>
    /// Batch/block parallelism (default 4). Values below 1 are treated as 1.
    /// Batch runs never create more workers than items; a single pack/extract
    /// additionally fans out the independent 64 KiB blocks, capped by the
    /// processor count, and parallel output stays byte-identical.
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Delete a pack source after a successful pack (default false). Off by
    /// default: a library should not destroy inputs unless explicitly asked.
    /// </summary>
    public bool DeleteSourceOnSuccess { get; set; }

    /// <summary>Pause gate checked alongside the cancellation token.</summary>
    public PauseToken Pause { get; set; }

    /// <summary>
    /// Preferred name-table order, or null for pack order (default). When set,
    /// the writer pre-seeds its deduplicated name list with these names so the
    /// on-disk name table follows this order instead of first appearance in
    /// pack order. Used for byte-parity with packers that write names in
    /// source-walk (discovery) order — e.g. the XISO <c>--zar</c> bridge mirrors
    /// XboxKit, which walks the XDVDFS btree in-order.
    /// </summary>
    public IReadOnlyList<string>? NameOrder { get; set; }

    internal IZarBlockCompressor ResolveCompressor()
    {
        return Compressor ?? new ZstdCompressor(new ZstdCompressionOptions
            { Level = Level, ChecksumFlag = Checksum, Dictionary = Dictionary });
    }

    /// <summary>
    /// One compressor per block-compression worker, or null to pack
    /// sequentially. Only the default level/checksum/dictionary configuration
    /// qualifies: an explicit <see cref="Compressor"/> (including
    /// <c>ZarRawCompressor</c>) stays on the single-threaded path because a
    /// foreign implementation's thread-safety is unknown — and raw storage is
    /// a memcpy that parallelism cannot speed up anyway.
    /// </summary>
    internal Func<IZarBlockCompressor>? ResolveCompressorFactory()
    {
        if (Compressor is not null)
        {
            return null;
        }

        var options = new ZstdCompressionOptions
            { Level = Level, ChecksumFlag = Checksum, Dictionary = Dictionary };
        return () => new ZstdCompressor(options);
    }

    /// <summary>
    /// Block-level fan-out for a single pack/extract: at least 1, at most the
    /// processor count (oversubscribing a CPU-bound codec only adds churn).
    /// </summary>
    internal int BlockWorkers()
    {
        return Math.Min(Math.Max(1, MaxDegreeOfParallelism), Environment.ProcessorCount);
    }

    internal int ClampedWorkers(int items)
    {
        return Math.Min(Math.Max(1, MaxDegreeOfParallelism), Math.Max(1, items));
    }
}