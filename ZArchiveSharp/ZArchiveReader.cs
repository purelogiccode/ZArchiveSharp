using System.Buffers;
using ZArchiveSharp.Zstd;

namespace ZArchiveSharp;

/// <summary>
/// Pure-C# ZArchive reader. Faithful port of <c>src/zarchivereader.cpp</c>
/// (ZArchive 0.1.2): same open-validation chain (returns null on any
/// failure, never throws), LRU block cache (4 MiB by default), case-insensitive
/// lookup, and the 0.1.2 long-name quirk (see <see cref="GetName(byte[], uint)"/>).
/// Thread-safe for concurrent reads: different blocks decompress in parallel,
/// only cache bookkeeping and copies take the lock.
/// </summary>
public sealed class ZArchiveReader : IDisposable
{
    /// <summary>Node handle returned when a path is not found.</summary>
    public const uint InvalidNode = ZArchiveCommon.InvalidNode;

    /// <summary>Node handle of the archive root directory (always 0).</summary>
    public const uint RootNode = 0;

    /// <summary>Directory entry (mirrors <c>ZArchiveReader::DirEntry</c>).</summary>
    public readonly struct DirEntry
    {
        /// <summary>Entry name (Windows-1252 decoded).</summary>
        public readonly string Name;

        /// <summary>True for files.</summary>
        public readonly bool IsFile;

        /// <summary>True for directories.</summary>
        public readonly bool IsDirectory;

        /// <summary>File size (valid for files only; 0 for directories).</summary>
        public readonly ulong Size;

        /// <summary>Creates an entry.</summary>
        public DirEntry(string name, bool isFile, ulong size)
        {
            Name = name;
            IsFile = isFile;
            IsDirectory = !isFile;
            Size = size;
        }
    }

    /// <summary>One cached decompressed 64 KiB block with its block index.</summary>
    private sealed class CacheBlock
    {
        /// <summary>Decompressed block data (always 64 KiB).</summary>
        public readonly byte[] Data = new byte[ZArchiveCommon.CompressedBlockSize];

        /// <summary>Cached block index, or <see cref="ulong.MaxValue"/> when empty.</summary>
        public ulong BlockIndex = ulong.MaxValue;
    }

#if NET9_0_OR_GREATER
    private readonly Lock _mutex = new();
#else
    private readonly object _mutex = new();
#endif
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _decodeExtendedNames;

    private readonly CompressionOffsetRecord[] _offsetRecords;
    private readonly byte[] _nameTable;
    private readonly FileDirectoryEntry[] _fileTree;
    private readonly ulong _compressedDataOffset;
    private readonly ulong _compressedDataSize;
    private readonly ulong _blockCount;

    private readonly LinkedList<CacheBlock> _lruChain = new();
    private readonly Dictionary<ulong, LinkedListNode<CacheBlock>> _blockLookup = [];
    private bool _disposed;

    /// <summary>
    /// Number of file-tree entries (files and directories). Computed from the
    /// in-memory tree at open, so it is cheap to query and cannot disagree with
    /// node handles.
    /// </summary>
    public uint EntryCount { get; }

    /// <summary>
    /// Total uncompressed size of every file in the archive. Computed from the
    /// in-memory tree at open (no archive I/O), so it is cheap to query; for a
    /// mounted volume this is the natural "volume size".
    /// </summary>
    public ulong TotalUncompressedSize { get; }

    /// <summary>
    /// Dictionary for decoding dictionary-compressed blocks (default null =
    /// plain archives, current behavior). Set before reading when the archive
    /// was packed with <c>ZarPipelineOptions.Dictionary</c>; like
    /// <c>zstd -D</c>, the dictionary lives outside the archive. A supplied
    /// dictionary is inert for plain (non-dictionary) blocks. Dictionary
    /// blocks read without a dictionary fail the read (the block decode
    /// returns false), they never return wrong bytes.
    /// </summary>
    public ZstdDictionary? Dictionary { get; set; }

    private ZArchiveReader(
        Stream stream, bool leaveOpen, ZArchiveReaderOptions options,
        CompressionOffsetRecord[] offsetRecords, byte[] nameTable, FileDirectoryEntry[] fileTree,
        ulong compressedDataOffset, ulong compressedDataSize)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _decodeExtendedNames = options.DecodeExtendedNames;
        _offsetRecords = offsetRecords;
        _nameTable = nameTable;
        _fileTree = fileTree;
        _compressedDataOffset = compressedDataOffset;
        _compressedDataSize = compressedDataSize;
        _blockCount = (ulong)offsetRecords.Length * (ulong)ZArchiveCommon.EntriesPerOffsetRecord;
        EntryCount = (uint)fileTree.Length;
        TotalUncompressedSize = SumUncompressedSize(fileTree);

        // LRU cache: CacheBlockCount x 64 KiB blocks (4 MiB at the default).
        for (var i = 0; i < options.CacheBlockCount; i++)
        {
            _lruChain.AddLast(new CacheBlock());
        }
    }

    private static ulong SumUncompressedSize(FileDirectoryEntry[] fileTree)
    {
        ulong total = 0;
        foreach (var entry in fileTree)
        {
            if (entry.IsFile)
            {
                total += entry.GetFileSize();
            }
        }

        return total;
    }

    // ------------------------------------------------------------------
    // Opening (null on any failure, no exceptions escape)
    // ------------------------------------------------------------------

    /// <summary>Opens an archive from a file. Returns null when invalid.</summary>
    public static ZArchiveReader? TryOpen(string path)
    {
        return TryOpen(path, ZArchiveReaderOptions.Default, out _);
    }

    /// <summary>
    /// Opens an archive from a file with explicit options. Returns null when invalid.
    /// </summary>
    /// <param name="path">The archive file to open.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    public static ZArchiveReader? TryOpen(string path, ZArchiveReaderOptions options)
    {
        return TryOpen(path, options, out _);
    }

    /// <summary>
    /// Opens an archive from a file, reporting why an invalid archive failed.
    /// Returns null when invalid.
    /// </summary>
    /// <param name="path">The archive file to open.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    public static ZArchiveReader? TryOpen(string path, out ZArchiveOpenFailure failure)
    {
        return TryOpen(path, ZArchiveReaderOptions.Default, out failure);
    }

    /// <summary>
    /// Opens an archive from a file with explicit options, reporting why an
    /// invalid archive failed. Returns null when invalid.
    /// </summary>
    /// <param name="path">The archive file to open.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> is null.</exception>
    public static ZArchiveReader? TryOpen(string path, ZArchiveReaderOptions options,
        out ZArchiveOpenFailure failure)
    {
        ArgumentNullException.ThrowIfNull(options);
        failure = ZArchiveOpenFailure.None;

        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, options.FileShare, 65536, false);
        }
        catch (FileNotFoundException)
        {
            failure = ZArchiveOpenFailure.FileNotFound;
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            failure = ZArchiveOpenFailure.FileNotFound;
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            failure = ZArchiveOpenFailure.AccessDenied;
            return null;
        }
        catch (IOException)
        {
            failure = ZArchiveOpenFailure.ReadError;
            return null;
        }
        catch (ArgumentException)
        {
            // Null, empty, or invalid path characters.
            failure = ZArchiveOpenFailure.FileNotFound;
            return null;
        }
        catch (Exception)
        {
            // Keep the "never throws" contract for anything else (security,
            // unsupported path forms, ...).
            failure = ZArchiveOpenFailure.ReadError;
            return null;
        }

        var reader = TryOpenCore(fs, leaveOpen: false, options, out failure);
        if (reader is null)
        {
            fs.Dispose();
        }

        return reader;
    }

    /// <summary>
    /// Opens an archive from a seekable stream. Returns null when invalid.
    /// On failure a stream not opened with <c>leaveOpen</c> is disposed:
    /// ownership is only transferred to the reader on success (the same
    /// cleanup <see cref="TryOpen(string)"/> applies).
    /// </summary>
    public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen = false)
    {
        return TryOpen(stream, leaveOpen, ZArchiveReaderOptions.Default, out _);
    }

    /// <summary>
    /// Opens an archive from a seekable stream with explicit options.
    /// Returns null when invalid.
    /// </summary>
    /// <param name="stream">The seekable archive stream.</param>
    /// <param name="leaveOpen">Keep the stream open after a failed open.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen, ZArchiveReaderOptions options)
    {
        return TryOpen(stream, leaveOpen, options, out _);
    }

    /// <summary>
    /// Opens an archive from a seekable stream, reporting why an invalid
    /// archive failed. Returns null when invalid.
    /// </summary>
    /// <param name="stream">The seekable archive stream.</param>
    /// <param name="leaveOpen">Keep the stream open after a failed open.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen, out ZArchiveOpenFailure failure)
    {
        return TryOpen(stream, leaveOpen, ZArchiveReaderOptions.Default, out failure);
    }

    /// <summary>
    /// Opens an archive from a seekable stream with explicit options, reporting
    /// why an invalid archive failed. Returns null when invalid.
    /// </summary>
    /// <param name="stream">The seekable archive stream.</param>
    /// <param name="leaveOpen">Keep the stream open after a failed open.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> is null.</exception>
    public static ZArchiveReader? TryOpen(Stream stream, bool leaveOpen, ZArchiveReaderOptions options,
        out ZArchiveOpenFailure failure)
    {
        ArgumentNullException.ThrowIfNull(options);

        var reader = TryOpenCore(stream, leaveOpen, options, out failure);
        if (reader is null && !leaveOpen && stream is not null)
        {
            try
            {
                stream.Dispose();
            }
            catch (Exception disposeEx)
            {
                // Cleanup only: the open result stays "null".
                _ = disposeEx;
            }
        }

        return reader;
    }

    private static ZArchiveReader? TryOpenCore(Stream stream, bool leaveOpen, ZArchiveReaderOptions options,
        out ZArchiveOpenFailure failure)
    {
        failure = ZArchiveOpenFailure.None;
        try
        {
            if (stream?.CanRead != true || !stream.CanSeek)
            {
                failure = ZArchiveOpenFailure.InvalidStream;
                return null;
            }

            var fileSize = (ulong)stream.Length;
            if (fileSize <= (ulong)Footer.SizeOnDisk)
            {
                failure = ZArchiveOpenFailure.TooSmall;
                return null;
            }

            var footerBytes = new byte[Footer.SizeOnDisk];
            if (!TryReadAt(stream, (long)(fileSize - (ulong)Footer.SizeOnDisk), footerBytes, 0, footerBytes.Length))
            {
                failure = ZArchiveOpenFailure.ReadError;
                return null;
            }

            var footer = Footer.ReadFrom(footerBytes);
            if (footer.Magic != Footer.KMagic)
            {
                failure = ZArchiveOpenFailure.BadMagic;
                return null;
            }

            if (footer.Version != Footer.KVersion1)
            {
                failure = ZArchiveOpenFailure.UnsupportedVersion;
                return null;
            }

            if (footer.TotalSize != fileSize)
            {
                failure = ZArchiveOpenFailure.LengthMismatch;
                return null;
            }

            if (!footer.SectionCompressedData.IsWithinValidRange(fileSize) ||
                !footer.SectionOffsetRecords.IsWithinValidRange(fileSize) ||
                !footer.SectionNames.IsWithinValidRange(fileSize) ||
                !footer.SectionFileTree.IsWithinValidRange(fileSize) ||
                !footer.SectionMetaDirectory.IsWithinValidRange(fileSize) ||
                !footer.SectionMetaData.IsWithinValidRange(fileSize))
            {
                failure = ZArchiveOpenFailure.SectionOutOfRange;
                return null;
            }

            if (footer.SectionOffsetRecords.Size > 0xFFFFFFFFUL)
            {
                failure = ZArchiveOpenFailure.BadOffsetRecords;
                return null;
            }

            if (footer.SectionNames.Size > 0x7FFFFFFFUL)
            {
                failure = ZArchiveOpenFailure.BadNameTable;
                return null;
            }

            if (footer.SectionFileTree.Size > 0xFFFFFFFFUL)
            {
                failure = ZArchiveOpenFailure.BadFileTree;
                return null;
            }

            // Offset records (must be a whole, non-empty count).
            if (footer.SectionOffsetRecords.Size % (ulong)CompressionOffsetRecord.SizeOnDisk != 0)
            {
                failure = ZArchiveOpenFailure.BadOffsetRecords;
                return null;
            }

            var numOffsetRecords =
                (long)(footer.SectionOffsetRecords.Size / (ulong)CompressionOffsetRecord.SizeOnDisk);
            if (numOffsetRecords == 0 || footer.SectionOffsetRecords.Size > int.MaxValue)
            {
                failure = ZArchiveOpenFailure.BadOffsetRecords;
                return null;
            }

            var offsetBytes = new byte[(int)footer.SectionOffsetRecords.Size];
            if (!TryReadAt(stream, (long)footer.SectionOffsetRecords.Offset, offsetBytes, 0, offsetBytes.Length))
            {
                failure = ZArchiveOpenFailure.ReadError;
                return null;
            }

            var offsetRecords = new CompressionOffsetRecord[numOffsetRecords];
            for (long i = 0; i < numOffsetRecords; i++)
            {
                offsetRecords[i] = CompressionOffsetRecord.ReadFrom(
                    offsetBytes.AsSpan((int)(i * CompressionOffsetRecord.SizeOnDisk)));
            }

            // Name table.
            if (footer.SectionNames.Size > int.MaxValue)
            {
                failure = ZArchiveOpenFailure.BadNameTable;
                return null;
            }

            var nameTable = new byte[(int)footer.SectionNames.Size];
            if (nameTable.Length > 0 &&
                !TryReadAt(stream, (long)footer.SectionNames.Offset, nameTable, 0, nameTable.Length))
            {
                failure = ZArchiveOpenFailure.ReadError;
                return null;
            }

            // File tree (must be a whole, non-empty count).
            if (footer.SectionFileTree.Size % (ulong)FileDirectoryEntry.SizeOnDisk != 0)
            {
                failure = ZArchiveOpenFailure.BadFileTree;
                return null;
            }

            var numEntries = (long)(footer.SectionFileTree.Size / (ulong)FileDirectoryEntry.SizeOnDisk);
            if (numEntries == 0 || numEntries > int.MaxValue || footer.SectionFileTree.Size > int.MaxValue)
            {
                failure = ZArchiveOpenFailure.BadFileTree;
                return null;
            }

            var treeBytes = new byte[(int)footer.SectionFileTree.Size];
            if (!TryReadAt(stream, (long)footer.SectionFileTree.Offset, treeBytes, 0, treeBytes.Length))
            {
                failure = ZArchiveOpenFailure.ReadError;
                return null;
            }

            var fileTree = new FileDirectoryEntry[numEntries];
            for (long i = 0; i < numEntries; i++)
            {
                fileTree[i] = FileDirectoryEntry.ReadFrom(
                    treeBytes.AsSpan((int)(i * FileDirectoryEntry.SizeOnDisk)));
            }

            // Verify root: first entry must be a directory with an empty name.
            if (fileTree[0].IsFile)
            {
                failure = ZArchiveOpenFailure.BadFileTree;
                return null;
            }

            if (GetName(nameTable, fileTree[0].NameOffset).Length != 0)
            {
                failure = ZArchiveOpenFailure.BadFileTree;
                return null;
            }

            return new ZArchiveReader(
                stream, leaveOpen, options, offsetRecords, nameTable, fileTree,
                footer.SectionCompressedData.Offset, footer.SectionCompressedData.Size);
        }
        catch
        {
            // Keep the most specific reason assigned before the fault; a plain
            // probe failure (I/O, allocation) surfaces as ReadError.
            if (failure == ZArchiveOpenFailure.None)
            {
                failure = ZArchiveOpenFailure.ReadError;
            }

            return null;
        }
    }

    /// <summary>Opens an archive from a byte array. Returns null when invalid.</summary>
    public static ZArchiveReader? TryOpen(byte[] data)
    {
        return TryOpen(data, ZArchiveReaderOptions.Default, out _);
    }

    /// <summary>Opens an archive from a byte array with explicit options. Returns null when invalid.</summary>
    /// <param name="data">The complete archive bytes.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    public static ZArchiveReader? TryOpen(byte[] data, ZArchiveReaderOptions options)
    {
        return TryOpen(data, options, out _);
    }

    /// <summary>
    /// Opens an archive from a byte array, reporting why an invalid archive
    /// failed. Returns null when invalid or when <paramref name="data"/> is null.
    /// </summary>
    /// <param name="data">The complete archive bytes.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    public static ZArchiveReader? TryOpen(byte[] data, out ZArchiveOpenFailure failure)
    {
        return TryOpen(data, ZArchiveReaderOptions.Default, out failure);
    }

    /// <summary>
    /// Opens an archive from a byte array with explicit options, reporting why
    /// an invalid archive failed. Returns null when invalid.
    /// </summary>
    /// <param name="data">The complete archive bytes.</param>
    /// <param name="options">Open options; see <see cref="ZArchiveReaderOptions"/>.</param>
    /// <param name="failure">Receives <see cref="ZArchiveOpenFailure.None"/> on success, or the reason.</param>
    /// <exception cref="ArgumentNullException">When <paramref name="options"/> is null.</exception>
    public static ZArchiveReader? TryOpen(byte[] data, ZArchiveReaderOptions options,
        out ZArchiveOpenFailure failure)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (data is null)
        {
            failure = ZArchiveOpenFailure.InvalidStream;
            return null;
        }

        // The reader keeps the stream; a read-only MemoryStream avoids copies.
        return TryOpen(new MemoryStream(data, writable: false), leaveOpen: false, options, out failure);
    }

    private static bool TryReadAt(Stream stream, long offset, byte[] buffer, int bufferOffset, int count)
    {
        try
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, bufferOffset + total, count - total);
                if (read == 0)
                {
                    return false;
                }

                total += read;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Names (with the 0.1.2 extended-length quirk)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decodes a name-table entry. Ports the 0.1.2 quirk exactly: in the
    /// extended 2-byte-length branch the length is computed from
    /// <c>nameTable[nameOffset]</c> (the FIRST header byte) again instead of
    /// <c>nameTable[nameOffset + 1]</c>. Names of ≥ 0x80 chars therefore
    /// decode to "" (upstream bug, preserved for byte parity).
    /// Returns "" on any out-of-range input.
    /// </summary>
    public static string GetName(byte[] nameTable, uint nameOffset)
    {
        return GetName(nameTable, nameOffset, decodeExtendedLengths: false);
    }

    /// <summary>
    /// Decodes a name-table entry, optionally correcting the 0.1.2
    /// extended-length quirk. With <paramref name="decodeExtendedLengths"/> set
    /// to <see langword="true"/> the second header byte is used for the length
    /// bits, so names of ≥ 0x80 chars decode correctly instead of to "".
    /// Returns "" on any out-of-range input.
    /// </summary>
    /// <param name="nameTable">The archive name table.</param>
    /// <param name="nameOffset">Name offset from a file-tree entry.</param>
    /// <param name="decodeExtendedLengths">Use the corrected 2-byte length decode.</param>
    public static string GetName(byte[] nameTable, uint nameOffset, bool decodeExtendedLengths)
    {
        if (nameOffset == ZArchiveCommon.RootNameOffset || nameOffset >= (uint)nameTable.Length)
        {
            return string.Empty;
        }

        var offset = (int)nameOffset;
        var nameLength = nameTable[offset] & 0x7F;
        if ((nameTable[offset] & 0x80) != 0)
        {
            // Extended 2-byte length (with the upstream quirk).
            if (offset + 1 >= nameTable.Length)
            {
                return string.Empty;
            }

            nameLength |= (decodeExtendedLengths ? nameTable[offset + 1] : nameTable[offset]) << 7;
            offset += 2;
        }
        else
        {
            offset++;
        }

        if (nameLength < 0 || offset + nameLength > nameTable.Length)
        {
            return string.Empty;
        }

        return ZArchiveCommon.Decode1252(nameTable.AsSpan(offset, nameLength));
    }

    /// <summary>Raw (Windows-1252) name bytes, or null when out of range.</summary>
    public static byte[]? GetNameRaw(byte[] nameTable, uint nameOffset, out int length)
    {
        return GetNameRaw(nameTable, nameOffset, out length, decodeExtendedLengths: false);
    }

    /// <summary>
    /// Raw (Windows-1252) name bytes, or null when out of range. With
    /// <paramref name="decodeExtendedLengths"/> set to <see langword="true"/>
    /// the corrected 2-byte length decode is used (see
    /// <see cref="GetName(byte[], uint, bool)"/>), so names of ≥ 0x80 chars
    /// resolve instead of silently failing.
    /// </summary>
    /// <param name="nameTable">The archive name table.</param>
    /// <param name="nameOffset">Name offset from a file-tree entry.</param>
    /// <param name="length">Receives the decoded name length in bytes.</param>
    /// <param name="decodeExtendedLengths">Use the corrected 2-byte length decode.</param>
    public static byte[]? GetNameRaw(byte[] nameTable, uint nameOffset, out int length,
        bool decodeExtendedLengths)
    {
        length = 0;
        if (nameOffset == ZArchiveCommon.RootNameOffset || nameOffset >= (uint)nameTable.Length)
        {
            return null;
        }

        var offset = (int)nameOffset;
        var nameLength = nameTable[offset] & 0x7F;
        if ((nameTable[offset] & 0x80) != 0)
        {
            if (offset + 1 >= nameTable.Length)
            {
                return null;
            }

            nameLength |= (decodeExtendedLengths ? nameTable[offset + 1] : nameTable[offset]) << 7;
            offset += 2;
        }
        else
        {
            offset++;
        }

        if (nameLength < 0 || offset + nameLength > nameTable.Length)
        {
            return null;
        }

        length = nameLength;
        return nameTable.AsSpan(offset, nameLength).ToArray();
    }

    // ------------------------------------------------------------------
    // Lookup & directory/file operations
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves <paramref name="path"/> to a node handle, or
    /// <see cref="InvalidNode"/> when not found.
    /// </summary>
    /// <remarks>
    /// <paramref name="allowFile"/>/<paramref name="allowDirectory"/> are
    /// accepted for API compatibility but ignored, exactly like the
    /// reference C++ (which never reads them).
    /// </remarks>
    public uint LookUp(string path, bool allowFile = true, bool allowDirectory = true)
    {
        _ = allowFile;
        _ = allowDirectory;
        if (path is null)
        {
            return InvalidNode;
        }

        // Byte-faithful walk: encode the path as Windows-1252 (separators
        // are ASCII and survive the codec) and compare raw name bytes.
        var pathBytes = ZArchiveCommon.Encode1252(path.AsSpan());
        var pos = 0;
        uint currentNode = 0;
        while (true)
        {
            // Skip leading separators.
            while (pos < pathBytes.Length && (pathBytes[pos] == (byte)'/' || pathBytes[pos] == (byte)'\\'))
            {
                pos++;
            }

            if (pos >= pathBytes.Length)
            {
                return currentNode; // end of path
            }

            var nodeStart = pos;
            while (pos < pathBytes.Length && pathBytes[pos] != (byte)'/' && pathBytes[pos] != (byte)'\\')
            {
                pos++;
            }

            ReadOnlySpan<byte> nodeName = pathBytes.AsSpan(nodeStart, pos - nodeStart);
            if (currentNode >= (uint)_fileTree.Length)
            {
                return InvalidNode;
            }

            var entry = _fileTree[currentNode];
            if (entry.IsFile)
            {
                return InvalidNode; // trying to iterate a file
            }

            var index = entry.NodeStartIndex;
            // Subtraction form: NodeStartIndex + Count can wrap a crafted
            // entry; require the whole child range inside the table.
            if (index > (uint)_fileTree.Length || entry.Count > (uint)_fileTree.Length - index)
            {
                return InvalidNode;
            }

            var endIndex = index + entry.Count;
            var match = InvalidNode;
            while (index < endIndex)
            {
                if (index >= (uint)_fileTree.Length)
                {
                    return InvalidNode;
                }

                var child = _fileTree[index];
                var childName = GetNameRaw(_nameTable, child.NameOffset, out var childLen, _decodeExtendedNames);
                if (childName is not null &&
                    ZArchiveCommon.CompareNodeNameBool(nodeName, childName.AsSpan(0, childLen)))
                {
                    match = index;
                    break;
                }

                index++;
            }

            if (match == InvalidNode)
            {
                return InvalidNode;
            }

            currentNode = match;
        }
    }

    /// <summary>True when <paramref name="node"/> is a directory.</summary>
    public bool IsDirectory(uint node)
    {
        return node < (uint)_fileTree.Length && !_fileTree[node].IsFile;
    }

    /// <summary>True when <paramref name="node"/> is a file.</summary>
    public bool IsFile(uint node)
    {
        return node < (uint)_fileTree.Length && _fileTree[node].IsFile;
    }

    /// <summary>
    /// Child count (0 for files and invalid handles). The raw stored count is
    /// clamped to the file-tree bounds, so a crafted directory entry can never
    /// make callers iterate past the table.
    /// </summary>
    public uint GetDirEntryCount(uint node)
    {
        if (node >= (uint)_fileTree.Length || _fileTree[node].IsFile)
        {
            return 0;
        }

        var dir = _fileTree[node];
        if (dir.NodeStartIndex > (uint)_fileTree.Length)
        {
            return 0;
        }

        var available = (uint)_fileTree.Length - dir.NodeStartIndex;
        return Math.Min(dir.Count, available);
    }

    /// <summary>Reads a directory entry. Returns false when invalid.</summary>
    public bool GetDirEntry(uint node, uint index, out DirEntry entry)
    {
        entry = default;
        if (node >= (uint)_fileTree.Length || _fileTree[node].IsFile)
        {
            return false;
        }

        var dir = _fileTree[node];
        if (index >= dir.Count)
        {
            return false;
        }

        // Subtraction form: NodeStartIndex + index can wrap a crafted entry
        // onto an unrelated in-range node; require the sum inside the table.
        if (dir.NodeStartIndex > (uint)_fileTree.Length
            || index >= (uint)_fileTree.Length - dir.NodeStartIndex)
        {
            return false;
        }

        var childIndex = dir.NodeStartIndex + index;
        if (childIndex >= (uint)_fileTree.Length)
        {
            return false;
        }

        var child = _fileTree[childIndex];
        var name = GetName(_nameTable, child.NameOffset, _decodeExtendedNames);
        if (name.Length == 0)
        {
            return false; // bad name (also rejects the ≥0x80-char quirk names)
        }

        entry = new DirEntry(name, child.IsFile, child.IsFile ? child.GetFileSize() : 0);
        return true;
    }

    /// <summary>
    /// Reads a directory entry together with the child's node handle, so
    /// callers (mounts, extractors, tree walks) can descend without rebuilding
    /// a path and looking it up again. Returns false when invalid.
    /// </summary>
    /// <param name="node">Directory node handle.</param>
    /// <param name="index">Zero-based child index.</param>
    /// <param name="childNode">Receives the child's node handle.</param>
    /// <param name="entry">Receives the child's name, type, and size.</param>
    public bool TryGetDirEntry(uint node, uint index, out uint childNode, out DirEntry entry)
    {
        childNode = InvalidNode;
        if (!GetDirEntry(node, index, out entry))
        {
            return false;
        }

        childNode = _fileTree[node].NodeStartIndex + index;
        return true;
    }

    /// <summary>
    /// Returns the canonical (stored) name of a node handle. The root's name is
    /// the empty string; unresolved quirk names decode to the empty string
    /// unless the archive was opened with
    /// <see cref="ZArchiveReaderOptions.DecodeExtendedNames"/>.
    /// </summary>
    /// <param name="node">Node handle to name.</param>
    /// <param name="name">Receives the decoded name.</param>
    /// <returns><see langword="true"/> when <paramref name="node"/> is in range.</returns>
    public bool TryGetNodeName(uint node, out string name)
    {
        if (node >= (uint)_fileTree.Length)
        {
            name = string.Empty;
            return false;
        }

        name = GetName(_nameTable, _fileTree[node].NameOffset, _decodeExtendedNames);
        return true;
    }

    /// <summary>File size (0 for directories and invalid handles).</summary>
    public ulong GetFileSize(uint node)
    {
        if (node >= (uint)_fileTree.Length || !_fileTree[node].IsFile)
        {
            return 0;
        }

        return _fileTree[node].GetFileSize();
    }

    /// <summary>
    /// Reads up to <c>buffer.Length</c> bytes from <paramref name="node"/>
    /// at <paramref name="offset"/> (clamped to the file size). Returns the
    /// number of bytes read; a block failure mid-read returns the partial
    /// count (short read) instead of looking like EOF. Thread-safe: cache
    /// bookkeeping and copies are locked, block decompression is not, so
    /// concurrent reads of distinct blocks run in parallel.
    /// </summary>
    public ulong ReadFromFile(uint node, ulong offset, Span<byte> buffer)
    {
        if (node >= (uint)_fileTree.Length)
        {
            return 0;
        }

        ulong fileOffset;
        ulong bytesToRead;
        ulong rawReadOffset;
        lock (_mutex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var file = _fileTree[node];
            if (!file.IsFile)
            {
                return 0;
            }

            fileOffset = file.GetFileOffset();
            var fileSize = file.GetFileSize();
            if (offset >= fileSize)
            {
                return 0;
            }

            bytesToRead = Math.Min((ulong)buffer.Length, fileSize - offset);
            rawReadOffset = fileOffset + offset;
        }

        var remaining = bytesToRead;
        var bufferPos = 0;
        while (remaining > 0)
        {
            var blockIndex = rawReadOffset / (ulong)ZArchiveCommon.CompressedBlockSize;
            var blockOffset = (int)(rawReadOffset % (ulong)ZArchiveCommon.CompressedBlockSize);
            var step = (int)Math.Min(remaining, (ulong)ZArchiveCommon.CompressedBlockSize - (ulong)blockOffset);
            if (!TryCopyBlock(blockIndex, blockOffset, buffer.Slice(bufferPos, step)))
            {
                // A failed block after some bytes were copied must not
                // look like EOF: report the partial read so callers see
                // a short read (and ReadFile turns it into an error)
                // instead of silently dropping the remainder.
                return (ulong)bufferPos;
            }

            rawReadOffset += (ulong)step;
            remaining -= (ulong)step;
            bufferPos += step;
        }

        return bytesToRead;
    }

    /// <summary>
    /// Copies <c>destination.Length</c> bytes of decompressed block
    /// <paramref name="blockIndex"/> starting at <paramref name="blockOffset"/>.
    /// Cache hits copy under the cache lock; misses read and decompress outside
    /// the lock (distinct blocks decode in parallel) and then publish the
    /// block. Returns false (short read) when the block is unreadable or corrupt.
    /// </summary>
    private bool TryCopyBlock(ulong blockIndex, int blockOffset, Span<byte> destination)
    {
        lock (_mutex)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_blockLookup.TryGetValue(blockIndex, out var node))
            {
                MarkBlockAsMru(node);
                node.Value.Data.AsSpan(blockOffset, destination.Length).CopyTo(destination);
                return true;
            }
        }

        // Miss: decode outside the lock into a pooled scratch buffer, then
        // publish it (or reuse the copy another thread published meanwhile).
        var scratch = ArrayPool<byte>.Shared.Rent(ZArchiveCommon.CompressedBlockSize);
        try
        {
            if (!TryDecodeBlock(blockIndex, scratch))
            {
                return false;
            }

            lock (_mutex)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_blockLookup.TryGetValue(blockIndex, out var existing))
                {
                    MarkBlockAsMru(existing);
                    existing.Value.Data.AsSpan(blockOffset, destination.Length).CopyTo(destination);
                    return true;
                }

                var recycled = _lruChain.First!;
                _blockLookup.Remove(recycled.Value.BlockIndex);
                recycled.Value.BlockIndex = blockIndex;
                scratch.AsSpan(0, ZArchiveCommon.CompressedBlockSize).CopyTo(recycled.Value.Data);
                _blockLookup[blockIndex] = recycled;
                MarkBlockAsMru(recycled);
                recycled.Value.Data.AsSpan(blockOffset, destination.Length).CopyTo(destination);
                return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Reads a whole file into a new array (empty when invalid).</summary>
    public byte[] ReadFile(uint node)
    {
        var size = GetFileSize(node);
        if (size > int.MaxValue)
        {
            throw new InvalidOperationException("File too large to read into memory.");
        }

        var buffer = new byte[(int)size];
        var read = ReadFromFile(node, 0, buffer);
        if (read != size)
        {
            throw new IOException("Failed to read file from archive.");
        }

        return buffer;
    }

    /// <summary>
    /// Opens a seekable, read-only <see cref="Stream"/> over a file node's
    /// uncompressed contents. Reads go through the block cache; disposing the
    /// stream does not dispose the archive (the reader stays usable).
    /// </summary>
    /// <param name="node">A file node handle (from <see cref="LookUp"/> or <see cref="TryGetDirEntry"/>).</param>
    /// <returns>A stream positioned at byte 0 of the file.</returns>
    /// <exception cref="ArgumentException">
    /// When <paramref name="node"/> is out of range or refers to a directory.
    /// </exception>
    public Stream OpenRead(uint node)
    {
        if (node >= (uint)_fileTree.Length || !_fileTree[node].IsFile)
        {
            throw new ArgumentException("Node handle does not refer to a file in this archive.", nameof(node));
        }

        return new EntryStream(this, node, _fileTree[node].GetFileSize());
    }

    /// <summary>
    /// Opens a seekable, read-only <see cref="Stream"/> over the file found at
    /// <paramref name="path"/>. Returns <see langword="null"/> when the path is
    /// missing, a directory, or otherwise not a file.
    /// </summary>
    /// <param name="path">Archive path using <c>/</c> or <c>\</c> separators.</param>
    public Stream? TryOpenRead(string path)
    {
        var node = LookUp(path);
        if (node == InvalidNode || !IsFile(node))
        {
            return null;
        }

        return OpenRead(node);
    }

    /// <summary>
    /// Seekable read-only stream over one file node. Holds no lock between
    /// calls; individual instances are not thread-safe (standard Stream
    /// semantics), while the underlying reader is.
    /// </summary>
    private sealed class EntryStream : Stream
    {
        private readonly ZArchiveReader _reader;
        private readonly uint _node;
        private long _position;
        private bool _disposed;

        public EntryStream(ZArchiveReader reader, uint node, ulong length)
        {
            _reader = reader;
            _node = node;
            Length = length > long.MaxValue ? long.MaxValue : (long)length;
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length { get; }

        public override long Position
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _position;
            }
            set
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _position = Math.Clamp(value, 0, Length);
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var remaining = Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = (int)_reader.ReadFromFile(_node, (ulong)_position, buffer[..toRead]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
            }

            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------
    // Block cache
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the global uncompressed offset and size of a file node.
    /// Thread-safe: the file tree is immutable after opening.
    /// </summary>
    internal bool TryGetFileRange(uint node, out ulong offset, out ulong size)
    {
        offset = 0;
        size = 0;
        if (node >= (uint)_fileTree.Length || !_fileTree[node].IsFile)
        {
            return false;
        }

        offset = _fileTree[node].GetFileOffset();
        size = _fileTree[node].GetFileSize();
        return true;
    }

    /// <summary>
    /// Decodes one global 64 KiB block into <paramref name="destination"/>
    /// (which must hold <c>CompressedBlockSize</c> bytes at
    /// <paramref name="destinationOffset"/>). Thread-safe and independent of
    /// the LRU cache, so concurrent calls for distinct blocks scale: only the
    /// compressed-slice stream read holds the mutex, never the decode itself.
    /// Returns false (never throws) when the block is out of range,
    /// unreadable, or corrupt.
    /// </summary>
    internal bool TryDecodeBlock(ulong blockIndex, byte[] destination, int destinationOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (blockIndex >= _blockCount ||
            destinationOffset < 0 ||
            destination.Length - destinationOffset < ZArchiveCommon.CompressedBlockSize)
        {
            return false;
        }

        var recordIndex = blockIndex / (ulong)ZArchiveCommon.EntriesPerOffsetRecord;
        var recordSubIndex = blockIndex % (ulong)ZArchiveCommon.EntriesPerOffsetRecord;
        if (recordIndex >= (ulong)_offsetRecords.Length)
        {
            return false;
        }

        var record = _offsetRecords[recordIndex];
        var offset = record.BaseOffset;
        for (ulong i = 0; i < recordSubIndex; i++)
        {
            offset += (ulong)record.Sizes[i] + 1;
        }

        var compressedSize = (uint)record.Sizes[recordSubIndex] + 1;
        if (offset + compressedSize > _compressedDataSize)
        {
            return false;
        }

        // Single read under the set-before-reading contract.
        var dict = Dictionary;
        var fileOffset = _compressedDataOffset + offset;
        if (compressedSize == (uint)ZArchiveCommon.CompressedBlockSize)
        {
            lock (_mutex)
            {
                return !_disposed && TryReadAt(_stream, (long)fileOffset,
                    destination, destinationOffset, ZArchiveCommon.CompressedBlockSize);
            }
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)compressedSize);
        try
        {
            lock (_mutex)
            {
                if (_disposed || !TryReadAt(_stream, (long)fileOffset, rented, 0, (int)compressedSize))
                {
                    return false;
                }
            }

            try
            {
                ZstdDecompressor.DecompressExact(rented, 0, (int)compressedSize,
                    destination, destinationOffset, ZArchiveCommon.CompressedBlockSize, dict);
                return true;
            }
            catch (ZstdException)
            {
                return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void MarkBlockAsMru(LinkedListNode<CacheBlock> node)
    {
        if (node.List is null || _lruChain.Last == node)
        {
            return; // already MRU
        }

        _lruChain.Remove(node);
        _lruChain.AddLast(node);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_mutex)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (!_leaveOpen)
        {
            _stream.Dispose();
        }
    }
}