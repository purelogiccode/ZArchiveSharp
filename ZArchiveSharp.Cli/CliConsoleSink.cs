using Serilog.Core;
using Serilog.Events;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Renders each event as one plain line on stdout — or stderr when the
/// <c>ToStderr</c> property is set — preserving the oracle-parity console
/// bytes exactly (no timestamps, no level tags). Events marked
/// <c>NoConsole</c> (inline progress fragments) skip the console.
/// </summary>
internal sealed class CliConsoleSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (logEvent.Properties.ContainsKey(CliLog.SilentProperty))
        {
            return;
        }

        var stderr = logEvent.Properties.TryGetValue(CliLog.StderrProperty, out var raw)
            && raw is ScalarValue { Value: true };
        var text = logEvent.RenderMessage();
        if (stderr)
        {
            Console.Error.WriteLine(text);
        }
        else
        {
            Console.WriteLine(text);
        }
    }
}
