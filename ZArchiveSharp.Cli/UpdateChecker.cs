using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Best-effort GitHub release check for the CLI. <see cref="Begin"/> starts
/// the lookup in the background when the process opens; <see cref="Notify"/>
/// runs after the command finishes, so startup and command latency are never
/// blocked by the network. A newer release is announced on stderr (never
/// stdout, which may carry binary data) with an opt-in browser redirect to
/// the platform asset (<c>release_&lt;version&gt;_win-x64.zip</c> /
/// <c>release_&lt;version&gt;_win-arm64.zip</c>) or the release page. Honors
/// the <c>ZAR_BUG_REPORT=off</c> master switch; all failures are swallowed.
/// CLI binary project only.
/// </summary>
internal static class UpdateChecker
{
    private const string RepoUrl = "https://github.com/purelogiccode/ZArchiveSharp";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/purelogiccode/ZArchiveSharp/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>A release as needed for the update notice.</summary>
    /// <param name="Tag">Release tag, e.g. <c>v1.1.0</c>.</param>
    /// <param name="PageUrl">Release page on GitHub.</param>
    /// <param name="AssetName">Matching platform asset name, when one exists.</param>
    /// <param name="AssetUrl">Direct download URL of <paramref name="AssetName"/>.</param>
    internal sealed record ReleaseInfo(string Tag, string PageUrl, string? AssetName, string? AssetUrl);

    /// <summary>
    /// Starts the background release lookup; the task carries
    /// <see langword="null"/> when the master telemetry switch disabled it.
    /// </summary>
    internal static Task<ReleaseInfo?> Begin()
    {
        if (BugReportSink.TelemetryDisabled)
        {
            return Task.FromResult<ReleaseInfo?>(null);
        }

        try
        {
            return Task.Run(CheckAsync);
        }
        catch (Exception beginEx)
        {
            // Update checks must never affect the CLI.
            _ = beginEx;
            return Task.FromResult<ReleaseInfo?>(null);
        }
    }

    /// <summary>
    /// Reports <paramref name="check"/> when it already completed and a
    /// strictly newer release exists; a still-running check is skipped, so
    /// command latency and exit are never blocked by the network. Optionally
    /// prompts (interactive only) to open the platform download or release
    /// page. Never throws.
    /// </summary>
    internal static void Notify(Task<ReleaseInfo?> check, bool quiet)
    {
        if (quiet || !check.IsCompleted)
        {
            return;
        }

        ReleaseInfo? release;
        try
        {
            release = check.Result;
        }
        catch (Exception notifyEx)
        {
            // Offline, throttled, or canceled: no notice.
            _ = notifyEx;
            return;
        }

        if (release is null)
        {
            return;
        }

        var current = ParseVersion(Program.GetVersion());
        var latest = ParseVersion(release.Tag);
        if (current is null || latest is null || latest <= current)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"A newer ZArchiveSharp.Cli is available: {release.Tag} (running {Program.GetVersion()}).");
            Console.Error.WriteLine($"  Release page: {release.PageUrl}");
            if (release.AssetUrl is { Length: > 0 })
            {
                Console.Error.WriteLine($"  Platform download: {release.AssetUrl}");
            }

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                return;
            }

            var target = release.AssetUrl ?? release.PageUrl;
            var prompt = release.AssetName is { Length: > 0 }
                ? $"Download {release.AssetName} in your browser? [y/N] "
                : "Open the release page in your browser? [y/N] ";
            Console.Error.Write(prompt);
            var answer = Console.ReadLine()?.Trim();
            if (answer is not null
                && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                    || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
            {
                OpenBrowser(target);
            }
        }
        catch (Exception ioEx)
        {
            // A broken console must never change the exit code.
            _ = ioEx;
        }
    }

    private static async Task<ReleaseInfo?> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("ZArchiveSharp.Cli/" + Program.GetVersion());
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (Exception checkEx)
        {
            // Offline, rate-limited, or malformed response: stay silent.
            _ = checkEx;
            return null;
        }
    }

    private static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("tag_name", out var tagProperty)
            || tagProperty.GetString() is not { Length: > 0 } tag)
        {
            return null;
        }

        var pageUrl = root.TryGetProperty("html_url", out var pageProperty)
            ? pageProperty.GetString()
            : null;
        if (string.IsNullOrEmpty(pageUrl))
        {
            pageUrl = $"{RepoUrl}/releases/tag/{tag}";
        }

        string? assetName = null;
        string? assetUrl = null;
        if (PlatformAssetSuffix() is { } suffix
            && root.TryGetProperty("assets", out var assets)
            && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object
                    || !asset.TryGetProperty("name", out var nameProperty))
                {
                    continue;
                }

                var name = nameProperty.GetString();
                if (name is null
                    || !name.StartsWith("release_", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith($"_{suffix}.zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                assetName = name;
                assetUrl = asset.TryGetProperty("browser_download_url", out var urlProperty)
                    ? urlProperty.GetString()
                    : null;
                break;
            }
        }

        return new ReleaseInfo(tag, pageUrl, assetName, assetUrl);
    }

    private static string? PlatformAssetSuffix()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => null
        };
    }

    private static Version? ParseVersion(string raw)
    {
        var text = raw.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V'))
        {
            text = text[1..];
        }

        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        return Version.TryParse(text, out var version) ? version : null;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception openEx)
        {
            // No browser or handler: the printed URL is enough.
            _ = openEx;
        }
    }
}
