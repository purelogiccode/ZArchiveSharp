using Serilog;
using Serilog.Events;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Single logging front-end for the CLI binary: every message flows through
/// Serilog (plain-line console sink for oracle-parity stdout/stderr bytes,
/// bug-report sink for Warning+). Stdout vs stderr is chosen per call, so
/// binary-stdout modes stay uncorrupted. Logging never throws: a broken
/// console still yields the normal exit code, never a logging crash.
/// </summary>
internal static class CliLog
{
    /// <summary>Serilog property forcing an event to stderr.</summary>
    internal const string StderrProperty = "ToStderr";

    /// <summary>Serilog property keeping an event off the console.</summary>
    internal const string SilentProperty = "NoConsole";

    /// <summary>Informational line on stdout (normal chatter).</summary>
    public static void Out(string message)
    {
        Emit(LogEventLevel.Information, stderr: false, message);
    }

    /// <summary>Blank line on stdout.</summary>
    public static void Out()
    {
        Out(string.Empty);
    }

    /// <summary>Warning on stdout (oracle-parity lines that are still reported).</summary>
    public static void WarnToStdout(string message)
    {
        Emit(LogEventLevel.Warning, stderr: false, message);
    }

    /// <summary>Error line on stderr (reported to the bug API).</summary>
    public static void Err(string message)
    {
        Emit(LogEventLevel.Error, stderr: true, message);
    }

    /// <summary>Error line on stderr, attaching <paramref name="ex"/> when one was captured.</summary>
    public static void Err(string message, Exception? ex)
    {
        if (ex is null)
        {
            Err(message);
        }
        else
        {
            Emit(LogEventLevel.Error, stderr: true, message, ex);
        }
    }

    /// <summary>Informational line on stderr (cancels, binary-stdout chatter: not reported).</summary>
    public static void InfoToStderr(string message)
    {
        Emit(LogEventLevel.Information, stderr: true, message);
    }

    /// <summary>Fatal line on stderr with its exception attached (always reported).</summary>
    public static void Fatal(string message, Exception ex)
    {
        try
        {
            Log.ForContext(StderrProperty, true).Fatal(ex, "{Msg:l}", message);
        }
        catch (Exception fatalEx)
        {
            // Logging must never crash the CLI; there is nowhere to report a logging failure to.
            _ = fatalEx;
        }
    }

    /// <summary>
    /// Inline progress fragment (no newline, <c>\r</c>-style): written to the
    /// console as-is and recorded as a Serilog debug event (never reported).
    /// </summary>
    public static void Progress(string text)
    {
        try
        {
            Log.ForContext(SilentProperty, true).Debug("{Msg:l}", text);
        }
        catch (Exception progressEx)
        {
            // Logging must never crash the CLI; there is nowhere to report a logging failure to.
            _ = progressEx;
        }

        Console.Write(text);
    }

    private static void Emit(LogEventLevel level, bool stderr, string message, Exception? ex = null)
    {
        try
        {
            Log.ForContext(StderrProperty, stderr).Write(level, ex, "{Msg:l}", message);
        }
        catch (Exception emitEx)
        {
            // Logging must never crash the CLI; there is nowhere to report a logging failure to.
            _ = emitEx;
        }
    }
}
