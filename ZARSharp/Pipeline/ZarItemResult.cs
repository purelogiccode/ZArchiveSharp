namespace ZARSharp.Pipeline;

/// <summary>
/// Result of one batch item. Ports <c>ProcessResult</c>
/// </summary>
public sealed record ZarItemResult(
    string SourcePath,
    string? DestinationPath,
    ZarItemStatus Status,
    string? ErrorMessage = null,
    long FilesProcessed = 0,
    long BytesProcessed = 0);
