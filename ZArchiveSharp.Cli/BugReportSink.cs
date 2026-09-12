using System.Collections.Concurrent;
using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Forwards Warning+ events to the PureLogicCode bug-report API
/// (<c>POST /bugreport/api/send-bug-report</c>) on a background sender, so
/// reporting never blocks or throws on the logging path. Client-side rate
/// limiting (9 sends/minute) stays under the server's 10/min/IP cap;
/// overflow is dropped. Set <c>ZAR_BUG_REPORT=off</c> to disable (the test
/// harness does this so parity runs never touch the production API).
/// CLI binary project only.
/// </summary>
internal sealed class BugReportSink : ILogEventSink, IDisposable
{
    private const string Endpoint = "https://www.purelogiccode.com/bugreport/api/send-bug-report";

    // Submit-only key (rate-limited); see InstructionsToSendBugs.md in
    // AspNet_BugReportEmailService. It can only file reports, nothing else.
    // The key is double-obfuscated in ApiKeyProvider and decoded on first use.
    private static string ApiKey => ApiKeyProvider.ApiKey;

    private const int MaxPerMinute = 9;
    private const int QueueCapacity = 64;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Master telemetry switch for the CLI binary (bug reports, usage stats
    /// and update checks): set <c>ZAR_BUG_REPORT=off</c>, or pass
    /// <c>--no-telemetry</c> (which calls <see cref="DisableTelemetry"/>),
    /// to disable all outbound telemetry. The test harness sets the
    /// environment variable so automation never pollutes the production
    /// endpoints.
    /// </summary>
    internal static bool TelemetryDisabled =>
        _disabled || Environment.GetEnvironmentVariable("ZAR_BUG_REPORT")?.Trim()
            .ToLowerInvariant() is "off" or "0" or "false" or "no";

    private static volatile bool _disabled;

    /// <summary>Process-wide opt-out used by <c>--no-telemetry</c>.</summary>
    internal static void DisableTelemetry()
    {
        _disabled = true;
    }

    private readonly BlockingCollection<LogEvent> _queue = new(QueueCapacity);
    private readonly Task _sender;
    private readonly Queue<DateTimeOffset> _sendTimes = new();
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private bool _disposed;

    public BugReportSink()
    {
        _sender = Task.Run(SendLoopAsync);
    }

    public void Emit(LogEvent logEvent)
    {
        if (TelemetryDisabled || logEvent.Level < LogEventLevel.Warning)
        {
            return;
        }

        try
        {
            _queue.TryAdd(logEvent);
        }
        catch (InvalidOperationException completedEx)
        {
            // Queue completed during shutdown: dropping is correct.
            _ = completedEx;
        }
        catch (Exception emitEx)
        {
            // Reporting must never throw on the logging path.
            _ = emitEx;
        }
    }

    /// <summary>
    /// Stops accepting reports and waits up to <paramref name="timeout"/> for
    /// the backlog to send. The wait is deliberately caller-bounded: reports
    /// are best-effort, so a slow endpoint must never delay process exit
    /// beyond this budget (the sender keeps running as a background task).
    /// </summary>
    public void Flush(TimeSpan timeout)
    {
        try
        {
            _queue.CompleteAdding();
            _sender.Wait(timeout);
        }
        catch (Exception flushEx)
        {
            // Best effort only.
            _ = flushEx;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _queue.CompleteAdding();
        }
        catch (Exception disposeEx)
        {
            // Best effort only.
            _ = disposeEx;
        }

        // Never block shutdown on an in-flight POST (the sender is a
        // background task). The queue is only disposed once the sender has
        // finished draining it, so no consumer races the disposal.
        try
        {
            if (_sender.Wait(TimeSpan.Zero))
            {
                _queue.Dispose();
            }
        }
        catch (Exception waitEx)
        {
            // Best effort only; the sender thread is background.
            _ = waitEx;
        }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            foreach (var evt in _queue.GetConsumingEnumerable())
            {
                await SendOneAsync(evt).ConfigureAwait(false);
            }
        }
        catch (Exception loopEx)
        {
            // The background reporter must never take down the CLI.
            _ = loopEx;
        }
    }

    private async Task SendOneAsync(LogEvent evt)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            while (_sendTimes.Count > 0 && now - _sendTimes.Peek() > TimeSpan.FromMinutes(1))
            {
                _sendTimes.Dequeue();
            }

            if (_sendTimes.Count >= MaxPerMinute)
            {
                return;
            }

            _sendTimes.Enqueue(now);
        }

        try
        {
            var payload = BuildPayload(evt.RenderMessage(), evt.Exception);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.TryAddWithoutValidation("X-API-KEY", ApiKey);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception sendEx)
        {
            // Offline, throttled, or server error: best effort only.
            _ = sendEx;
        }
    }

    private static string BuildPayload(string error, Exception? ex)
    {
        var sb = new StringBuilder();
        sb.Append("{\"message\":");
        BugReportFormatter.AppendJsonString(sb,
            BugReportFormatter.Sanitize(BugReportFormatter.FormatMessage(error, ex)));
        sb.Append(",\"applicationName\":");
        BugReportFormatter.AppendJsonString(sb, BugReportFormatter.ApplicationName);
        sb.Append(",\"version\":");
        BugReportFormatter.AppendJsonString(sb, BugReportFormatter.FormatVersion());
        sb.Append(",\"environment\":");
        BugReportFormatter.AppendJsonString(sb, BugReportFormatter.Sanitize(BugReportFormatter.FormatEnvironment()));
        sb.Append(",\"stackTrace\":");
        BugReportFormatter.AppendJsonString(sb, BugReportFormatter.Sanitize(BugReportFormatter.FormatStackTrace(ex)));
        sb.Append('}');
        return sb.ToString();
    }
}