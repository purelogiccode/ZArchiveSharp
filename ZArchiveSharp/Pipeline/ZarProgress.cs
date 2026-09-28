namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Progress event for pack/extract work. Totals are pre-scanned, so
/// <see cref="Ratio"/> moves monotonically from 0 to 1 within one
/// <c>SourcePath</c>; batch runs re-base it per item into the batch-wide
/// share (completed items plus the in-flight fraction).
/// </summary>
public readonly record struct ZarProgress(
    ZarOperation Operation,
    string SourcePath,
    string DestinationPath,
    string CurrentFile,
    long FilesCompleted,
    long FilesTotal,
    long BytesCompleted,
    long BytesTotal)
{
    /// <summary>Overall 0..1 fraction (bytes when known, else files).</summary>
    public double Ratio =>
        BytesTotal > 0 ? (double)BytesCompleted / BytesTotal :
        FilesTotal > 0 ? (double)FilesCompleted / FilesTotal : 1.0;
}