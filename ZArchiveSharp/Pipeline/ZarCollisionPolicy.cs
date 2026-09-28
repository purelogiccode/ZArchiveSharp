namespace ZArchiveSharp.Pipeline;

/// <summary>
/// What to do when a pipeline output path already exists.
/// <see cref="Fail"/> keeps the <c>zarchive.exe</c> contract (refuse to
/// overwrite an existing output, exit <c>-11</c>); the other policies support
/// batch runs: skip, overwrite, or auto-rename to <c>{stem}_{n}{suffix}</c>.
/// </summary>
public enum ZarCollisionPolicy
{
    /// <summary>Throw <see cref="IOException"/> when the output exists (default).</summary>
    Fail = 0,

    /// <summary>Skip the item, reporting <see cref="ZarItemStatus.Skipped"/>.</summary>
    Skip = 1,

    /// <summary>Delete the existing output before writing.</summary>
    Overwrite = 2,

    /// <summary>Write to <c>{stem}_{n}{suffix}</c>, first free <c>n</c> from 1.</summary>
    AutoRename = 3,
}
