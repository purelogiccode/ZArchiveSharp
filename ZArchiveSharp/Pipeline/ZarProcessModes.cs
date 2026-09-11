namespace ZArchiveSharp.Pipeline;

/// <summary>
/// CLI-facing name mapping for <see cref="ZarProcessMode"/> (the
/// <c>--mode</c> flag). Lives in the library so the mapping is unit-tested
/// without referencing the CLI project (which carries the cross-repo
/// XISOSharp source dependency).
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

        string name = value.Trim();
        if (string.Equals(name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZarProcessMode.Auto;
            return true;
        }

        if (string.Equals(name, "extract-archive", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "extract-arc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "archive", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZarProcessMode.ExtractArchive;
            return true;
        }

        if (string.Equals(name, "extract-iso", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "extract", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "iso", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZarProcessMode.ExtractIso;
            return true;
        }

        if (string.Equals(name, "compress", StringComparison.OrdinalIgnoreCase))
        {
            mode = ZarProcessMode.Compress;
            return true;
        }

        return false;
    }
}