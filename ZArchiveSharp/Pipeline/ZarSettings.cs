using System.Text.Json;

namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Persistent settings for a pipeline front-end. Defaults are applied when a
/// file is missing or unreadable, unknown/missing JSON members fall back to
/// defaults per property, and the default location is a per-user config
/// directory (with <see cref="AppContext.BaseDirectory"/> as the last
/// resort). Serialization uses a source-generated context so the
/// trimmable/AOT posture of the library is preserved.
/// </summary>
public sealed record ZarSettings
{
    /// <summary>Default batch source directory.</summary>
    public string SourceDir { get; init; } = "";

    /// <summary>Default batch target directory.</summary>
    public string TargetDir { get; init; } = "";

    /// <summary>Batch worker count (default 4, clamped to >= 1 on use).</summary>
    public int Workers { get; init; } = 4;

    /// <summary>UI language tag (default pt-br).</summary>
    public string Language { get; init; } = "pt-br";

    /// <summary>UI theme name (default Sistema).</summary>
    public string Theme { get; init; } = "Sistema";

    /// <summary>Whether the app self-updates (default true).</summary>
    public bool AutoUpdate { get; init; } = true;

    /// <summary>Last window geometry blob (default empty).</summary>
    public string WindowGeometry { get; init; } = "";

    /// <summary>Collision policy for batch outputs (default Fail).</summary>
    public ZarCollisionPolicy CollisionPolicy { get; init; } = ZarCollisionPolicy.Fail;

    /// <summary>Pipeline mode for batch runs (default Auto).</summary>
    public ZarProcessMode Mode { get; init; } = ZarProcessMode.Auto;

    /// <summary>
    /// Loads <c>settings.json</c> from <paramref name="directory"/> (or the
    /// default location): a missing file, unreadable file or broken JSON
    /// yields defaults.
    /// </summary>
    public static ZarSettings Load(string? directory = null, string fileName = "settings.json")
    {
        var path = Path.Combine(DefaultDirectory(directory), fileName);
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, ZarConfigJsonContext.Default.ZarSettings)
                   ?? new ZarSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            return new ZarSettings();
        }
    }

    /// <summary>Saves this config as <c>settings.json</c> (best effort).</summary>
    public void Save(string? directory = null, string fileName = "settings.json", Action<string>? log = null)
    {
        try
        {
            var dir = DefaultDirectory(directory);
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, ZarConfigJsonContext.Default.ZarSettings);
            File.WriteAllText(Path.Combine(dir, fileName), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke(ex.Message);
        }
    }

    /// <summary>Builds <see cref="ZarPipelineOptions"/> from worker/policy settings.</summary>
    public ZarPipelineOptions ToPipelineOptions()
    {
        return new ZarPipelineOptions
        {
            MaxDegreeOfParallelism = Workers,
            CollisionPolicy = CollisionPolicy,
        };
    }

    internal static string DefaultDirectory(string? overrideDirectory)
    {
        if (overrideDirectory is not null)
        {
            return overrideDirectory;
        }

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrEmpty(appData))
            {
                return Path.Combine(appData, "ZArchiveSharp");
            }
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home))
            {
                return Path.Combine(home, ".config", "zarchivesharp");
            }
        }

        return AppContext.BaseDirectory;
    }
}
