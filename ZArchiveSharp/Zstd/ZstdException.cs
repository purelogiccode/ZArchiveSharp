namespace ZArchiveSharp.Zstd;

#pragma warning disable MA0048 // File name must match type name — related types are grouped intentionally

/// <summary>Thrown when zstd input is corrupt or uses unsupported features.</summary>
public sealed class ZstdException : Exception
{
    /// <summary>Creates a decoder error.</summary>
    /// <param name="message">Reason.</param>
    public ZstdException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a decoder error with a default message.</summary>
    public ZstdException()
    {
    }

    /// <summary>Creates a decoder error with an inner cause.</summary>
    /// <param name="message">Reason.</param>
    /// <param name="innerException">Inner cause.</param>
    public ZstdException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Shared message fragments for dictionary failures, so "bad dictionary" is
/// distinguishable from "corrupt frame" at a glance. Call sites append the
/// concrete IDs, sizes, and offsets.
/// </summary>
internal static class ZstdErrorMessages
{
    /// <summary>A frame carries a dictionary ID but no dictionary was supplied.</summary>
    internal const string DictionaryRequired = "zstd frame requires a dictionary";

    /// <summary>A frame's dictionary ID does not match the supplied dictionary.</summary>
    internal const string DictionaryMismatch = "zstd dictionary ID mismatch";

    /// <summary>Dictionary bytes fail structural validation.</summary>
    internal const string DictionaryCorrupted = "Invalid zstd dictionary";

    /// <summary>Dictionary content exceeds the decoder's window limit.</summary>
    internal const string DictionaryTooLarge = "zstd dictionary exceeds decoder limit";
}