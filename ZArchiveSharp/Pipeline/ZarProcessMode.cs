namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Pipeline mode: <c>Auto</c> runs the full chain for each input, the others
/// run a single stage.
/// </summary>
public enum ZarProcessMode
{
    /// <summary>Full chain: archive, then XISO, then ZAR stage as applicable.</summary>
    Auto = 0,

    /// <summary>Archive-extraction stage only (7z and friends).</summary>
    ExtractArchive = 1,

    /// <summary>XISO-extraction stage only.</summary>
    ExtractIso = 2,

    /// <summary>ZAR-compression stage only.</summary>
    Compress = 3,
}
