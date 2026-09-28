using System.Diagnostics;
using System.Globalization;

namespace ZArchiveSharp.Pipeline;

/// <summary>
/// Runs an external tool and tracks its progress. The merged output stream is
/// scanned for <c>(\d+)%</c> progress lines at most every 100 ms, exit codes
/// <c>0</c> and <c>1</c> count as success (the latter is 7z's harmless
/// warning), anything else raises with the last output line attached, and
/// cancellation kills the process tree. This is the seam for external tools
/// (e.g. 7z); ZAR pack/extract itself runs in-process via
/// <see cref="ZarPipeline"/>.
/// </summary>
public static class ProcessRunner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Runs <paramref name="fileName"/> and returns its exit code plus last output line.</summary>
    /// <exception cref="FileNotFoundException">When the tool is missing or blocked.</exception>
    /// <exception cref="UnauthorizedAccessException">On Windows elevation error 740.</exception>
    /// <exception cref="InvalidOperationException">On nonzero (non-1) exit.</exception>
    public static ProcessResult Run(
        string fileName,
        string arguments = "",
        string? workingDirectory = null,
        IProgress<double>? progress = null,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Start(process, fileName);

        string? lastError = null;
        var stderrDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lastError = e.Data;
            }
            else
            {
                stderrDrained.TrySetResult();
            }
        };
        process.BeginErrorReadLine();

        string? lastOutput;
        try
        {
            lastOutput = PumpStdout(process, progress, pause, cancellationToken);
            WaitForExit(process, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            process.WaitForExit(5000);
            throw;
        }

        // The stderr pump is asynchronous: give it a bounded moment to reach
        // EOF so a late-only failure line is not reported as "no output". A
        // grandchild can inherit the pipe and keep it open past the tool's
        // exit: stop the pump instead of stalling on EOF (lines already read
        // stay in lastError).
        if (!stderrDrained.Task.Wait(TimeSpan.FromSeconds(5), cancellationToken))
        {
            TryCancelErrorRead(process);
        }

        var lastLine = lastOutput ?? lastError;
        ThrowIfFailed(process.ExitCode, lastLine, fileName);
        return new ProcessResult(process.ExitCode, lastLine);
    }

    private static void Start(Process process, string fileName)
    {
        try
        {
            if (process.Start())
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740)
        {
            throw new UnauthorizedAccessException(
                $"Required tool needs elevation (run as administrator): {fileName}", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new FileNotFoundException(
                $"Required tool not found or blocked by antivirus (allow-list it and retry): {fileName} ({ex.Message})",
                ex);
        }

        throw new FileNotFoundException($"Required tool did not start: {fileName}");
    }

    private static void WaitForExit(Process process, CancellationToken cancellationToken)
    {
        // Poll instead of WaitForExitAsync: the async wait can stay blocked
        // until the redirected pipes hit EOF, so a child that closed stdout
        // (PumpStdout returned) but keeps running would never observe
        // cancellation. Polling kills it here.
        while (!process.WaitForExit(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static string? PumpStdout(
        Process process, IProgress<double>? progress, PauseToken pause, CancellationToken cancellationToken)
    {
        // Read one character at a time so carriage-return progress bars turn
        // into one line each. 7z and friends report progress on stdout; stderr
        // is drained by the event pump and its last line is kept for failures.
        var line = new System.Text.StringBuilder();
        string? lastLine = null;
        var clock = Stopwatch.StartNew();
        var stdout = process.StandardOutput;
        while (true)
        {
            pause.WaitIfPaused(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var next = stdout.Read();
            if (next < 0)
            {
                break;
            }

            var ch = (char)next;
            if (ch is not ('\r' or '\n'))
            {
                line.Append(ch);
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            lastLine = Take(line);
            if (TryParseProgressLine(lastLine) is { } ratio &&
                (clock.Elapsed >= ProgressInterval || ratio >= 1.0))
            {
                progress?.Report(ratio);
                clock.Restart();
            }
        }

        if (line.Length > 0)
        {
            lastLine = Take(line);
            if (TryParseProgressLine(lastLine) is { } trailingRatio)
            {
                progress?.Report(trailingRatio);
            }
        }

        return lastLine;

        static string Take(System.Text.StringBuilder buffer)
        {
            var text = buffer.ToString();
            buffer.Clear();
            return text;
        }
    }

    /// <summary>
    /// Parses a <c>(\d+)%</c> progress line to 0..1 (leftmost match wins),
    /// else null. Hand-rolled (no regex backtracking surface).
    /// </summary>
    internal static double? TryParseProgressLine(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '%')
            {
                continue;
            }

            var start = i;
            while (start > 0 && char.IsAsciiDigit(line[start - 1]))
            {
                start--;
            }

            if (start == i)
            {
                continue;
            }

            if (int.TryParse(line.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var percent))
            {
                return Math.Clamp(percent / 100.0, 0.0, 1.0);
            }
        }

        return null;
    }

    /// <summary>Accepts exit 0/1, else throws with the last line attached.</summary>
    /// <exception cref="InvalidOperationException">On nonzero (non-1) exit.</exception>
    internal static void ThrowIfFailed(int exitCode, string? lastLine, string fileName)
    {
        if (exitCode is 0 or 1)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Tool failed with exit code {exitCode}: {fileName} ({lastLine ?? "no output"})");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            /* best effort */
        }
    }

    private static void TryCancelErrorRead(Process process)
    {
        try
        {
            process.CancelErrorRead();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            /* best effort */
        }
    }

    /// <summary>Exit code plus the last output line of a finished tool.</summary>
    public sealed record ProcessResult(int ExitCode, string? LastLine);
}