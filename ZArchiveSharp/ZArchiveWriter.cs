using System.Buffers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using ZArchiveSharp.Zstd;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

namespace ZArchiveSharp;

/// <summary>
/// Compresses one 64 KiB block. Returns the compressed size, or -1 to store
/// the block raw (uncompressed). Raw storage is always valid per the format.
/// </summary>
public interface IZarBlockCompressor
{
    /// <summary>Compresses exactly 64 KiB from <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <returns>Compressed size, or -1 to store raw.</returns>
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);
}

/// <summary>Default compressor: stores every block raw (no compression).</summary>
public sealed class ZarRawCompressor : IZarBlockCompressor
{
    /// <inheritdoc/>
    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        return -1;
    }
}

/// <summary>
/// Pure-C# ZArchive writer. Faithful port of <c>src/zarchivewriter.cpp</c>
/// (ZArchive 0.1.2). All integers big-endian on disk; paths Windows-1252,
/// case-insensitive (A-Z folding); names deduplicated case-sensitively.
/// </summary>
public sealed class ZArchiveWriter : IDisposable
{
    /// <summary>In-memory directory-tree node used while building the archive.</summary>
    private sealed class PathNode
    {
        /// <summary>True for file nodes, false for directories.</summary>
        public bool IsFile;

        /// <summary>Index into the deduplicated name list.</summary>
        public int NameIndex = -1;

        /// <summary>Child nodes (directories only).</summary>
        public readonly List<PathNode> Subnodes = [];

        /// <summary>Uncompressed input offset where the file starts.</summary>
        public ulong FileOffset;

        /// <summary>Uncompressed file size.</summary>
        public ulong FileSize;

        /// <summary>First child index in the serialized file tree.</summary>
        public uint NodeStartIndex;
    }

    private readonly Action<int> _newOutputFile;
    private readonly Action<byte[], int, int> _writeOutputData;
    private readonly IZarBlockCompressor _compressor;

    private readonly PathNode _rootNode = new();
    private PathNode? _currentFileNode;
    private readonly List<string> _nodeNames = [];
    private uint[] _nodeNameOffsets = [];
    private readonly Dictionary<string, uint> _nodeNameLookup = new(StringComparer.Ordinal);

    private Footer _footer;
    private readonly byte[] _currentWriteBuffer = new byte[ZArchiveCommon.CompressedBlockSize];

    private int _bufferedBytes;

    // Dedicated pump scratch: AppendData(Stream) must NOT reuse
    // _currentWriteBuffer, whose live tail would alias the chunk being
    // appended when the staging buffer is partially full.
    private readonly byte[] _streamPump = new byte[ZArchiveCommon.CompressedBlockSize];
    private readonly byte[] _compressionBuffer;
    private ulong _currentCompressedWriteIndex;
    private ulong _currentInputOffset;
    private ulong _numWrittenOffsetRecords;
    private readonly List<CompressionOffsetRecord> _offsetRecords = [];

    // Parallel block compression staging (opt-in via maxDegreeOfParallelism +
    // compressorFactory; off by default so the sequential path is untouched).
    // Full 64 KiB blocks are copied to rented buffers as they complete and
    // compressed out of order, but always emitted in input order — output
    // bytes are identical to sequential packing.
    private readonly int _blockWorkers;
    private readonly Func<IZarBlockCompressor>? _compressorFactory;
    private readonly int _flushThreshold;
    private readonly List<byte[]> _stagedBlocks = [];
    private List<BlockWorker>? _workers;

    /// <summary>Upper bound for one compressed 64 KiB block (per-block dest buffers).</summary>
    private static readonly int CompressBound =
        ZstdCompressor.GetCompressBound(ZArchiveCommon.CompressedBlockSize);

    private sealed class BlockWorker
    {
        public required IZarBlockCompressor Compressor;
    }

    private IncrementalHash? _sha;
    private bool _finalized;
    private bool _disposed;

    /// <summary>
    /// Creates a writer with output callbacks. <paramref name="newOutputFile"/>
    /// is invoked immediately with <c>-1</c> (mirrors the C++ ctor).
    /// </summary>
    /// <param name="newOutputFile">Invoked with the part index for each new output file.</param>
    /// <param name="writeOutputData">Appends raw bytes to the current output file.</param>
    /// <param name="compressor">Block compressor (default zstd level 6).</param>
    /// <param name="nameOrder">Pre-seeded name-table order, or null for pack order.</param>
    /// <param name="maxDegreeOfParallelism">
    /// Block-compression fan-out (default 1 = sequential, current behavior).
    /// Values above 1 take effect only with <paramref name="compressorFactory"/>
    /// (custom <see cref="IZarBlockCompressor"/> instances stay sequential:
    /// their thread-safety is unknown). Compressed bytes are always emitted
    /// in input order, so parallel output is byte-identical to sequential.
    /// </param>
    /// <param name="compressorFactory">
    /// Creates one compressor per worker (e.g. <code>() => new
    /// ZstdCompressor(options)</code>); <paramref name="compressor"/> remains the
    /// sequential-path instance and is never shared across workers.
    /// </param>
    public ZArchiveWriter(
        Action<int> newOutputFile,
        Action<byte[], int, int> writeOutputData,
        IZarBlockCompressor? compressor = null,
        IEnumerable<string>? nameOrder = null,
        int maxDegreeOfParallelism = 1,
        Func<IZarBlockCompressor>? compressorFactory = null)
    {
        _newOutputFile = newOutputFile ?? throw new ArgumentNullException(nameof(newOutputFile));
        _writeOutputData = writeOutputData ?? throw new ArgumentNullException(nameof(writeOutputData));
        _compressor = compressor ?? new ZstdCompressor();
        _compressionBuffer = new byte[ZstdCompressor.GetCompressBound(ZArchiveCommon.CompressedBlockSize)];
        _blockWorkers = Math.Max(1, maxDegreeOfParallelism);
        _compressorFactory = _blockWorkers > 1 ? compressorFactory : null;
        _flushThreshold = Math.Max(2, _blockWorkers * 2);
        _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (nameOrder != null)
        {
            foreach (var name in nameOrder)
            {
                CreateNameEntry(name);
            }
        }

        _newOutputFile(-1);
    }

    /// <summary>
    /// Creates a writer that appends to <paramref name="output"/>.
    /// <paramref name="nameOrder"/> pre-seeds the deduplicated name list so the
    /// name table follows that order instead of pack order (see
    /// <see cref="ZArchiveSharp.Pipeline.ZarPipelineOptions.NameOrder"/>).
    /// </summary>
    public ZArchiveWriter(Stream output, IZarBlockCompressor? compressor = null,
        IEnumerable<string>? nameOrder = null,
        int maxDegreeOfParallelism = 1,
        Func<IZarBlockCompressor>? compressorFactory = null)
        : this(
            _ => { },
            (buf, off, count) => output.Write(buf, off, count),
            compressor,
            nameOrder,
            maxDegreeOfParallelism,
            compressorFactory)
    {
    }

    // ------------------------------------------------------------------
    // Tree helpers
    // ------------------------------------------------------------------

    private PathNode? GetNodeByPath(string path)
    {
        var current = _rootNode;
        var parser = path.AsSpan();
        while (ZArchiveCommon.GetNextPathNode(ref parser, out var nodeName))
        {
            var next = FindSubnodeByName(current, nodeName);
            if (next?.IsFile != false)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private PathNode? FindSubnodeByName(PathNode parent, ReadOnlySpan<char> nodeName)
    {
        foreach (var child in parent.Subnodes)
        {
            if (ZArchiveCommon.CompareNodeNameBool(_nodeNames[child.NameIndex].AsSpan(), nodeName))
            {
                return child;
            }
        }

        return null;
    }

    private uint CreateNameEntry(ReadOnlySpan<char> name)
    {
        var key = name.ToString();
        if (_nodeNameLookup.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var index = (uint)_nodeNames.Count;
        _nodeNames.Add(key);
        _nodeNameLookup.Add(key, index);
        return index;
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a new virtual file and makes it active for
    /// <see cref="AppendData(ReadOnlySpan{byte})"/>. Returns false when the parent directory does
    /// not exist or the file already exists.
    /// </summary>
    public bool StartNewFile(string path)
    {
        _currentFileNode = null;
        var parser = path.AsSpan();
        ZArchiveCommon.SplitFilenameFromPath(ref parser, out var filename);
        var dir = GetNodeByPath(parser.ToString());
        if (dir is null)
        {
            return false;
        }

        if (FindSubnodeByName(dir, filename) is not null)
        {
            return false;
        }

        var node = new PathNode
        {
            IsFile = true,
            NameIndex = (int)CreateNameEntry(filename),
            FileOffset = _currentInputOffset,
        };
        dir.Subnodes.Add(node);
        _currentFileNode = node;
        return true;
    }

    /// <summary>
    /// Creates a directory. Non-recursive mode creates a single level;
    /// recursive mode creates all missing ancestors (fails if a file blocks
    /// the path). Trailing slashes are ignored.
    /// </summary>
    public bool MakeDir(string path, bool recursive = false)
    {
        var trimmed = path.TrimEnd('/', '\\');
        if (!recursive)
        {
            var parser = trimmed.AsSpan();
            ZArchiveCommon.SplitFilenameFromPath(ref parser, out var dirName);
            var dir = GetNodeByPath(parser.ToString());
            if (dir is null)
            {
                return false;
            }

            if (FindSubnodeByName(dir, dirName) is not null)
            {
                return false;
            }

            dir.Subnodes.Add(new PathNode { IsFile = false, NameIndex = (int)CreateNameEntry(dirName) });
            return true;
        }

        var current = _rootNode;
        var walk = trimmed.AsSpan();
        while (ZArchiveCommon.GetNextPathNode(ref walk, out var nodeName))
        {
            var next = FindSubnodeByName(current, nodeName);
            if (next?.IsFile == true)
            {
                return false;
            }

            if (next is null)
            {
                next = new PathNode { IsFile = false, NameIndex = (int)CreateNameEntry(nodeName) };
                current.Subnodes.Add(next);
            }

            current = next;
        }

        return true;
    }

    /// <summary>Appends data to the currently active file.</summary>
    public void AppendData(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
        {
            throw new InvalidOperationException("Archive already finalized.");
        }

        var dataSize = data.Length;
        var offset = 0;
        var remaining = dataSize;
        while (remaining > 0)
        {
            var bytesToCopy = ZArchiveCommon.CompressedBlockSize - _bufferedBytes;
            if (bytesToCopy > remaining)
            {
                bytesToCopy = remaining;
            }

            if (bytesToCopy == ZArchiveCommon.CompressedBlockSize)
            {
                // Block-aligned input bypasses the staging buffer (as in C++).
                if (_compressorFactory is null)
                {
                    StoreBlock(data.Slice(offset, bytesToCopy));
                }
                else
                {
                    // The caller's span is reused for the next read: copy.
                    StageBlock(data.Slice(offset, bytesToCopy));
                }

                offset += bytesToCopy;
                remaining -= bytesToCopy;
                continue;
            }

            data.Slice(offset, bytesToCopy).CopyTo(_currentWriteBuffer.AsSpan(_bufferedBytes));
            offset += bytesToCopy;
            remaining -= bytesToCopy;
            _bufferedBytes += bytesToCopy;
            if (_bufferedBytes == ZArchiveCommon.CompressedBlockSize)
            {
                if (_compressorFactory is null)
                {
                    StoreBlock(_currentWriteBuffer);
                }
                else
                {
                    StageBlock(_currentWriteBuffer);
                }

                _bufferedBytes = 0;
            }
        }

        if (_currentFileNode is not null)
        {
            _currentFileNode.FileSize += (ulong)dataSize;
        }

        _currentInputOffset += (ulong)dataSize;
    }

    /// <summary>Appends data to the currently active file.</summary>
    public void AppendData(byte[] data, int offset, int count)
    {
        AppendData(data.AsSpan(offset, count));
    }

    /// <summary>
    /// Appends all bytes from <paramref name="input"/> to the currently
    /// active file, so callers need not buffer entry streams manually.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="input"/> is null.</exception>
    /// <exception cref="ArgumentException">When <paramref name="input"/> is unreadable.</exception>
    public void AppendData(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(input));
        }

        // Fail fast on disposed/finalized even for empty input (the loop
        // below would otherwise never reach the span overload's checks).
        AppendData(ReadOnlySpan<byte>.Empty);

        int read;
        while ((read = input.Read(_streamPump, 0, _streamPump.Length)) > 0)
        {
            AppendData(_streamPump.AsSpan(0, read));
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private void OutputData(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var copy = data.ToArray();
        _writeOutputData(copy, 0, copy.Length);
        _currentCompressedWriteIndex += (ulong)copy.Length;
        _sha?.AppendData(copy);
    }

    private ulong GetCurrentOutputOffset()
    {
        return _currentCompressedWriteIndex;
    }

    private void StoreBlock(ReadOnlySpan<byte> uncompressedData)
    {
        var outputSize = _compressor.Compress(uncompressedData, _compressionBuffer.AsSpan());
        EmitBlock(uncompressedData, ZArchiveCommon.CompressedBlockSize, _compressionBuffer, outputSize);
    }

    /// <summary>
    /// Stages one full block for parallel compression (copied: the source
    /// span may be reused by the caller) and flushes once a full window is
    /// staged, keeping memory bounded while workers stay fed.
    /// </summary>
    private void StageBlock(ReadOnlySpan<byte> uncompressedData)
    {
        var copy = ArrayPool<byte>.Shared.Rent(ZArchiveCommon.CompressedBlockSize);
        uncompressedData.Slice(0, ZArchiveCommon.CompressedBlockSize).CopyTo(copy);
        _stagedBlocks.Add(copy);
        if (_stagedBlocks.Count >= _flushThreshold)
        {
            FlushStagedBlocks();
        }
    }

    /// <summary>
    /// Compresses all staged blocks across workers and emits them in input
    /// order. Compressor faults surface unwrapped (same contract as the
    /// sequential path: callers map exact exception types to exit codes).
    /// </summary>
    private void FlushStagedBlocks()
    {
        if (_stagedBlocks.Count == 0)
        {
            return;
        }

        EnsureWorkers(Math.Min(_blockWorkers, _stagedBlocks.Count));
        var workers = _workers!;
        var results = new int[_stagedBlocks.Count];
        // NOTE: destination buffers are per BLOCK, never per worker: a worker
        // compresses several blocks per flush and must not overwrite an
        // earlier block's output before it is emitted in order below.
        var dests = new byte[_stagedBlocks.Count][];
        try
        {
            for (var i = 0; i < dests.Length; i++)
            {
                dests[i] = ArrayPool<byte>.Shared.Rent(CompressBound);
            }

            try
            {
                Parallel.For(0, _stagedBlocks.Count,
                    new ParallelOptions { MaxDegreeOfParallelism = workers.Count },
                    i =>
                    {
                        var worker = workers[i % workers.Count];
                        // Explicit length: rented arrays may exceed the block size.
                        results[i] = worker.Compressor.Compress(
                            _stagedBlocks[i].AsSpan(0, ZArchiveCommon.CompressedBlockSize), dests[i]);
                    });
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count != 0)
            {
                ExceptionDispatchInfo.Throw(ex.InnerExceptions[0]);
            }

            for (var i = 0; i < _stagedBlocks.Count; i++)
            {
                EmitBlock(_stagedBlocks[i], ZArchiveCommon.CompressedBlockSize, dests[i], results[i]);
                ArrayPool<byte>.Shared.Return(_stagedBlocks[i]);
                // Detach immediately so a later EmitBlock fault cannot
                // double-return an already-returned buffer via Dispose.
                // The emit loop below removes the returned prefix on fault.
                _stagedBlocks[i] = null!;
            }

            _stagedBlocks.Clear();
        }
        catch
        {
            // Drop the already-returned prefix (nulled above); the faulting
            // entry and later ones stay queued for Dispose to return once.
            var emitted = 0;
            while (emitted < _stagedBlocks.Count && _stagedBlocks[emitted] is null)
            {
                emitted++;
            }

            if (emitted > 0)
            {
                _stagedBlocks.RemoveRange(0, emitted);
            }

            throw;
        }
        finally
        {
            foreach (var dest in dests)
            {
                if (dest is not null)
                {
                    ArrayPool<byte>.Shared.Return(dest);
                }
            }
        }
    }

    private void EnsureWorkers(int needed)
    {
        if (_compressorFactory is null)
        {
            return;
        }

        if (_workers is null)
        {
            _workers = CreateWorkers(Math.Max(1, needed));
            return;
        }

        while (_workers.Count < needed)
        {
            _workers.Add(new BlockWorker { Compressor = _compressorFactory() });
        }
    }

    private List<BlockWorker> CreateWorkers(int count)
    {
        var list = new List<BlockWorker>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(new BlockWorker { Compressor = _compressorFactory!() });
        }

        return list;
    }

    /// <summary>
    /// Emits one block (compressed or raw fallback) with its offset record.
    /// Single-threaded and order-sensitive: the only block-output path for
    /// both sequential and parallel packing, so their bytes are identical.
    /// </summary>
    private void EmitBlock(
        ReadOnlySpan<byte> raw, int rawLength,
        ReadOnlySpan<byte> compressed, int compressedSize)
    {
        var writeOffset = GetCurrentOutputOffset();
        int outputSize;
        if (compressedSize < 0 || compressedSize >= ZArchiveCommon.CompressedBlockSize)
        {
            // Store raw when incompressible (or when the compressor declines).
            OutputData(raw.Slice(0, rawLength));
            outputSize = ZArchiveCommon.CompressedBlockSize;
        }
        else
        {
            OutputData(compressed.Slice(0, compressedSize));
            outputSize = compressedSize;
        }

        if ((_numWrittenOffsetRecords % (ulong)ZArchiveCommon.EntriesPerOffsetRecord) == 0)
        {
            _offsetRecords.Add(new CompressionOffsetRecord(writeOffset));
        }

        var rec = _offsetRecords[^1];
        rec.Sizes[_numWrittenOffsetRecords % (ulong)ZArchiveCommon.EntriesPerOffsetRecord] =
            (ushort)(outputSize - 1);
        _offsetRecords[^1] = rec;
        _numWrittenOffsetRecords++;
    }

    /// <summary>
    /// Writes all sections and the footer. Pads the trailing partial block
    /// with zeros and the output to 8-byte alignment (as in C++).
    /// </summary>
    /// <remarks>
    /// Named to match the reference C++ API (<c>ZArchiveWriter::Finalize</c>);
    /// it is an ordinary method, not a finalizer.
    /// </remarks>
#pragma warning disable CS0465 // Finalize name mirrors the C++ API by design
    public void Finalize()
#pragma warning restore CS0465
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finalized)
        {
            return;
        }

        _currentFileNode = null; // padding must not grow the active file
        if (_bufferedBytes != 0)
        {
            var pad = ZArchiveCommon.CompressedBlockSize - _bufferedBytes;
            AppendData(new byte[pad]);
            _bufferedBytes = 0;
        }

        // Parallel path: emit everything staged (padding included) in order
        // before any section is written.
        FlushStagedBlocks();

        _finalized = true;

        _footer.SectionCompressedData = new OffsetInfo { Offset = 0, Size = GetCurrentOutputOffset() };
        while ((GetCurrentOutputOffset() % 8) != 0)
        {
            OutputData([0]);
        }

        WriteOffsetRecords();
        WriteNameTable();
        WriteFileTree();
        WriteMetaData();
        WriteFooter();
    }

    private void WriteOffsetRecords()
    {
        var start = GetCurrentOutputOffset();
        Span<byte> buf = stackalloc byte[CompressionOffsetRecord.SizeOnDisk];
        foreach (var rec in _offsetRecords)
        {
            rec.WriteTo(buf);
            OutputData(buf);
        }

        _footer.SectionOffsetRecords = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteNameTable()
    {
        var start = GetCurrentOutputOffset();
        _nodeNameOffsets = new uint[_nodeNames.Count];
        uint tableOffset = 0;
        Span<byte> header = stackalloc byte[2];
        for (var i = 0; i < _nodeNames.Count; i++)
        {
            _nodeNameOffsets[i] = tableOffset;
            // Match C++: truncate the name to 0x7FFF *characters* before
            // encoding (substr(0, 0x7FFF)), not post-encode bytes.
            var nameSpan = _nodeNames[i].AsSpan();
            if (nameSpan.Length > ZArchiveCommon.MaxNameLength)
            {
                nameSpan = nameSpan.Slice(0, ZArchiveCommon.MaxNameLength);
            }

            var nameBytes = ZArchiveCommon.Encode1252(nameSpan);

            if (nameBytes.Length >= 0x80)
            {
                header[0] = (byte)((nameBytes.Length & 0x7F) | 0x80);
                header[1] = (byte)(nameBytes.Length >> 7);
                OutputData(header);
                tableOffset += 2;
            }
            else
            {
                header[0] = (byte)(nameBytes.Length & 0x7F);
                OutputData(header.Slice(0, 1));
                tableOffset++;
            }

            OutputData(nameBytes);
            tableOffset += (uint)nameBytes.Length;
        }

        _footer.SectionNames = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteFileTree()
    {
        // First pass: assign directory node ranges (BFS from root, index 0).
        var queue = new Queue<PathNode>();
        queue.Enqueue(_rootNode);
        uint currentIndex = 1; // root node is at index 0
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.IsFile)
            {
                node.NodeStartIndex = 0xFFFFFFFF;
                continue;
            }

            // Ascending sort using the reversed-sign comparator (> 0 predicate).
            // C# Comparison needs negative when x < y, i.e. -Compare(x, y).
            node.Subnodes.Sort((a, b) =>
                -ZArchiveCommon.CompareNodeName(
                    _nodeNames[a.NameIndex].AsSpan(),
                    _nodeNames[b.NameIndex].AsSpan()));
            node.NodeStartIndex = currentIndex;
            currentIndex += (uint)node.Subnodes.Count;
            foreach (var child in node.Subnodes)
            {
                queue.Enqueue(child);
            }
        }

        // Second pass: serialize BFS.
        var start = GetCurrentOutputOffset();
        Span<byte> buf = stackalloc byte[FileDirectoryEntry.SizeOnDisk];
        var writeQueue = new Queue<PathNode>();
        writeQueue.Enqueue(_rootNode);
        while (writeQueue.Count > 0)
        {
            var node = writeQueue.Dequeue();
            var tmp = new FileDirectoryEntry();
            if (ReferenceEquals(node, _rootNode))
            {
                tmp.SetTypeAndNameOffset(node.IsFile, ZArchiveCommon.RootNameOffset);
            }
            else
            {
                tmp.SetTypeAndNameOffset(node.IsFile, _nodeNameOffsets[node.NameIndex]);
            }

            if (node.IsFile)
            {
                tmp.SetFileOffset(node.FileOffset);
                tmp.SetFileSize(node.FileSize);
            }
            else
            {
                tmp.Field1 = node.NodeStartIndex;
                tmp.Field2 = (uint)node.Subnodes.Count;
                tmp.Field3 = 0;
            }

            tmp.WriteTo(buf);
            OutputData(buf);
            foreach (var child in node.Subnodes)
            {
                writeQueue.Enqueue(child);
            }
        }

        _footer.SectionFileTree = new OffsetInfo
        {
            Offset = start,
            Size = GetCurrentOutputOffset() - start,
        };
    }

    private void WriteMetaData()
    {
        var now = GetCurrentOutputOffset();
        _footer.SectionMetaDirectory = new OffsetInfo { Offset = now, Size = 0 };
        _footer.SectionMetaData = new OffsetInfo { Offset = now, Size = 0 };
    }

    private void WriteFooter()
    {
        _footer.Magic = Footer.KMagic;
        _footer.Version = Footer.KVersion1;
        _footer.TotalSize = GetCurrentOutputOffset() + Footer.SizeOnDisk;
        _footer.IntegrityHash = new byte[32];

        // Hash the footer with zeroed integrity bytes (mirrors C++: the
        // context is closed before the real footer is written, so the final
        // footer bytes themselves are NOT hashed).
        Span<byte> tmp = stackalloc byte[Footer.SizeOnDisk];
        _footer.WriteTo(tmp);
        _sha!.AppendData(tmp.ToArray());
        var digest = _sha.GetHashAndReset();
        _sha.Dispose();
        _sha = null;

        _footer.IntegrityHash = (byte[])digest.Clone();
        _footer.WriteTo(tmp);

        // Raw write without hashing (ctx is null in C++ at this point).
        var finalFooter = tmp.ToArray();
        _writeOutputData(finalFooter, 0, finalFooter.Length);
        _currentCompressedWriteIndex += (ulong)finalFooter.Length;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var staged in _stagedBlocks)
        {
            // Null marks an already-returned prefix entry (emit fault path).
            if (staged is not null)
            {
                ArrayPool<byte>.Shared.Return(staged);
            }
        }

        _stagedBlocks.Clear();
        _sha?.Dispose();
        _sha = null;
    }
}