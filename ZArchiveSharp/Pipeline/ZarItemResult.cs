namespace ZArchiveSharp.Pipeline;

/// <summary>Result of one batch item, including byte/file counts.</summary>
public sealed record ZarItemResult(
    string SourcePath,
    string? DestinationPath,
    ZarItemStatus Status,
    string? ErrorMessage = null,
    long FilesProcessed = 0,
    long BytesProcessed = 0);
