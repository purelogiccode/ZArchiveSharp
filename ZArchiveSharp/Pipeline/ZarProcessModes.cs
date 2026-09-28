namespace ZArchiveSharp.Pipeline;

/// <summary>
/// CLI-facing name mapping for <see cref="ZarProcessMode"/> (the
/// <c>--mode</c> flag). Lives in the library so the mapping is unit-tested
/// without referencing the CLI project.
/// </summary>
public static class ZarProcessModes
{
    /// <summary>
    /// Parses a <c>--mode</c> value (case-insensitive, surrounding whitespace
    /// ignored). Accepted names: <c>auto</c>, <c>extract-archive</c>
    /// (<c>extract-arc</c>, <c>archive</c>), <c>extract-iso</c>
    /// (<c>extract</c>, <c>iso</c>), <c>compress</c>.
    /// </summary>
    /// <param name="value">Raw flag value.</param>
    /// <param name="mode">Parsed mode on success.</param>
    /// <returns>True when <paramref name="value"/> names a known mode.</returns>
    public static bool TryParse(string? value, out ZarProcessMode mode)
    {
        mode = ZarProcessMode.Auto;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                mode = ZarProcessMode.Auto;
                return true;
            case "extract-archive" or "extract-arc" or "archive":
                mode = ZarProcessMode.ExtractArchive;
                return true;
            case "extract-iso" or "extract" or "iso":
                mode = ZarProcessMode.ExtractIso;
                return true;
            case "compress":
                mode = ZarProcessMode.Compress;
                return true;
            default:
                return false;
        }
    }
}
