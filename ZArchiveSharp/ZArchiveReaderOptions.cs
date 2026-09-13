#pragma warning disable MA0048 // Related types are grouped intentionally

namespace ZArchiveSharp;

/// <summary>
/// Describes why <see cref="ZArchiveReader.TryOpen(string, out ZArchiveOpenFailure)"/>
/// (or one of its overloads) returned <see langword="null"/>. The plain
/// <c>TryOpen</c> overloads discard this information and simply return
/// <see langword="null"/> on any failure.
/// </summary>
public enum ZArchiveOpenFailure
{
    /// <summary>The archive opened successfully.</summary>
    None = 0,

    /// <summary>The file does not exist.</summary>
    FileNotFound,

    /// <summary>The file exists but the process is not allowed to read it.</summary>
    AccessDenied,

    /// <summary>The stream is null, unreadable, or not seekable.</summary>
    InvalidStream,

    /// <summary>An I/O error occurred while probing or reading the archive.</summary>
    ReadError,

    /// <summary>The input is not larger than the 144-byte footer.</summary>
    TooSmall,

    /// <summary>The footer magic does not match.</summary>
    BadMagic,

    /// <summary>The footer format version is not supported.</summary>
    UnsupportedVersion,

    /// <summary>The footer's total size does not match the input length.</summary>
    LengthMismatch,

    /// <summary>A footer section lies outside the input.</summary>
    SectionOutOfRange,

    /// <summary>The offset-record section is empty, truncated, or too large.</summary>
    BadOffsetRecords,

    /// <summary>The name table is too large to load.</summary>
    BadNameTable,

    /// <summary>The file tree is empty, truncated, or has an invalid root entry.</summary>
    BadFileTree,
}

/// <summary>
/// Tuning knobs for opening a <see cref="ZArchiveReader"/>. Immutable: create one
/// per open, or reuse <see cref="Default"/>.
/// </summary>
public sealed class ZArchiveReaderOptions
{
    /// <summary>
    /// Default options: 64 cached blocks (4 MiB), quirk-compatible name decoding,
    /// and <see cref="System.IO.FileShare.Read"/> when opening a path.
    /// </summary>
    public static ZArchiveReaderOptions Default { get; } = new();

    private readonly int _cacheBlockCount = 64;

    /// <summary>
    /// Number of decompressed 64 KiB blocks kept in the LRU cache
    /// (default 64 = 4 MiB). Raise it for archives read by many concurrent
    /// streams (for example a mounted drive); must be at least 1.
    /// </summary>
    public int CacheBlockCount
    {
        get => _cacheBlockCount;
        init => _cacheBlockCount = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(CacheBlockCount), value,
                "CacheBlockCount must be at least 1.");
    }

    /// <summary>
    /// Decodes the extended (&gt;= 0x80 character) name-table branch correctly.
    /// The default (<see langword="false"/>) preserves the upstream 0.1.2 quirk
    /// where such names decode to an empty string and are not resolvable; set to
    /// <see langword="true"/> to make those entries listable and readable.
    /// </summary>
    public bool DecodeExtendedNames { get; init; }

    /// <summary>
    /// Sharing mode used when opening a path (default
    /// <see cref="System.IO.FileShare.Read"/>). Use
    /// <see cref="System.IO.FileShare.ReadWrite"/> so other processes (scanners,
    /// indexers) can keep the archive open while it is mounted.
    /// </summary>
    public FileShare FileShare { get; init; } = FileShare.Read;
}
