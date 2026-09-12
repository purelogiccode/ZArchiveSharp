using System.Text;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Records one usage hit per launch with the PureLogicCode ApplicationStats
/// API (<c>POST /ApplicationStats/stats</c>; see
/// <c>InstructionsToUseApiEndpoints.md</c> in AspNet_ApplicationStats).
/// Fire-and-forget: never blocks startup, never throws, and honors the same
/// <c>ZAR_BUG_REPORT=off</c> telemetry switch as the bug-report sink (the
/// test harness sets it so automation never pollutes production stats).
/// The server rate-limits stats to 1 call/hour/IP; excess hits get HTTP 429
/// and are ignored. CLI binary project only.
/// </summary>
internal static class UsageTracker
{
    private const string Endpoint = "https://www.purelogiccode.com/ApplicationStats/stats";

    // Stats/bug shared secret (see InstructionsToUseApiEndpoints.md).
    private const string ApiKey = "hjh7yu6t56tyr540o9u8767676r5674534453235264c75b6t7ggghgg76trf564e";

    internal const string ApplicationId = "zarchivesharp-cli";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Queues one launch hit on the thread pool; returns immediately.</summary>
    public static void TrackLaunch()
    {
        if (BugReportSink.TelemetryDisabled)
        {
            return;
        }

        try
        {
            _ = Task.Run(SendAsync);
        }
        catch (Exception launchEx)
        {
            // Telemetry must never affect the CLI.
            _ = launchEx;
        }
    }

    private static async Task SendAsync()
    {
        try
        {
            var payload = BuildPayload();
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception sendEx)
        {
            // Offline, throttled (429), or server error: best effort only.
            _ = sendEx;
        }
    }

    private static string BuildPayload()
    {
        var sb = new StringBuilder();
        sb.Append("{\"applicationId\":");
        BugReportFormatter.AppendJsonString(sb, ApplicationId);
        sb.Append(",\"version\":");
        BugReportFormatter.AppendJsonString(sb, Program.GetVersion());
        sb.Append('}');
        return sb.ToString();
    }
}
