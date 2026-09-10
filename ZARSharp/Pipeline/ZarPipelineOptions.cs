using ZARSharp.Zstd;

namespace ZARSharp.Pipeline;

/// <summary>
/// Options for <see cref="ZarPipeline"/> pack/extract work. Defaults mirror
/// the two upstreams: zstd level 6 with no checksum (like
/// <c>zarchive.exe</c> / <c>ZArchiveTool</c>, keeping byte-identical output)
/// </summary>
public sealed class ZarPipelineOptions
{
    /// <summary>zstd level 1..22 for packing (default 6). Ignored for extract.</summary>
    public int Level { get; set; } = 6;

    /// <summary>Write per-block content checksums (default false, upstream parity).</summary>
    public bool Checksum { get; set; }

    /// <summary>
    /// Dictionary for pack block compression and archive extraction (default
    /// null = plain frames, current behavior). When set, pack writes
    /// dictionary frames (blocks where the dictionary pays; the rest stay
    /// raw) and extract requires the same dictionary for those blocks — like
    /// <c>zstd -D</c>, the dictionary file itself is never stored in the
    /// archive, so keep it alongside. A supplied dictionary is inert for
    /// plain frames, so extracting a plain archive with a dictionary set
    /// yields identical bytes. Ignored when <see cref="Compressor"/> is
    /// explicitly set (explicit compressor wins, as with
    /// <see cref="Level"/>).
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
    /// the actual worker count is <c>min(workers, items)</c> like
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>
    /// Delete a pack source directory after a successful pack (default false).
    /// because a library must not destroy inputs unless asked.
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

    internal IZarBlockCompressor ResolveCompressor() =>
        Compressor ?? new ZstdCompressor(new ZstdCompressionOptions { Level = Level, ChecksumFlag = Checksum, Dictionary = Dictionary });

    internal int ClampedWorkers(int items) =>
        Math.Min(Math.Max(1, MaxDegreeOfParallelism), Math.Max(1, items));
}
