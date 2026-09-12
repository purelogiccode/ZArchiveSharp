using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Regression tests for the best-effort telemetry paths: the update check and
/// the bug-report/usage senders must never delay the command or process exit.
/// A local TCP listener accepts the proxied traffic and never answers, so a
/// blocking implementation would wait out its full timeout (3 s update check,
/// 5 s report POST) while a non-blocking one exits promptly.
/// </summary>
public sealed class UpdateCheckLatencyTests
{
    // Generous bound: catches the multi-second timeout stalls without being
    // flaky on cold or loaded CI hosts (bounded telemetry waits total 750 ms).
    private const int MaxExitMs = 2500;

    [Fact]
    public async Task Command_WithHangingUpdateEndpoint_ExitsWithoutWaiting()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("latency_update");
        try
        {
            var input = Path.Combine(work, "in.bin");
            File.WriteAllBytes(input, new byte[64 * 1024]);
            var output = Path.Combine(work, "out.zst");

            var result = await RunWithHangingProxy(cli, work, ["zstd", "-c", input, output]);
            if (!result.Started)
            {
                return;
            }

            Assert.True(result.Exit == 0,
                $"The CLI failed (exit {result.Exit}) under a hanging update endpoint: {result.Stderr}");
            Assert.True(File.Exists(output));
            Assert.True(result.ElapsedMs < MaxExitMs,
                $"The CLI waited {result.ElapsedMs} ms on the update check (stderr: {result.Stderr}).");
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public async Task Error_WithHangingBugReportEndpoint_ExitsWithoutWaiting()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var work = RedumpIsoTests.NewTempDir("latency_report");
        try
        {
            // --jobs 0 is a usage error: it queues an Error event for the
            // bug-report sink and must still exit immediately.
            var result = await RunWithHangingProxy(cli, work, ["--jobs", "0"]);
            if (!result.Started)
            {
                return;
            }

            Assert.Equal(-1, result.Exit);
            Assert.Contains("--jobs", result.Stderr, StringComparison.Ordinal);
            Assert.True(result.ElapsedMs < MaxExitMs,
                $"The CLI waited {result.ElapsedMs} ms on the bug-report POST (stderr: {result.Stderr}).");
        }
        finally
        {
            TryDelete(work);
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // ignored: best-effort temp cleanup.
        }
    }

    private static async Task<(bool Started, int Exit, long ElapsedMs, string Stdout, string Stderr)>
        RunWithHangingProxy(string cli, string work, string[] args)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        using var stop = new CancellationTokenSource();
        var acceptor = AcceptWhileRunning(listener, held, stop.Token);

        try
        {
            var psi = BuildStartInfo(cli, args);
            psi.WorkingDirectory = work;
            // The point of the test is that telemetry is ENABLED and trying
            // to call out: remove the harness opt-out and route it to the
            // hanging proxy.
            psi.Environment.Remove("ZAR_BUG_REPORT");
            psi.Environment.Remove("NO_PROXY");
            psi.Environment.Remove("no_proxy");
            var proxy = $"http://127.0.0.1:{port}";
            psi.Environment["HTTP_PROXY"] = proxy;
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["http_proxy"] = proxy;
            psi.Environment["https_proxy"] = proxy;

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, -1, 0, string.Empty, string.Empty);
            }

            var clock = Stopwatch.StartNew();
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var exited = proc.WaitForExit(60_000);
            clock.Stop();
            Assert.True(exited, "The CLI did not exit.");
            return (true, proc.ExitCode, clock.ElapsedMilliseconds, stdout, stderr);
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            lock (held)
            {
                foreach (var client in held)
                {
                    client.Dispose();
                }
            }

            await acceptor.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task AcceptWhileRunning(TcpListener listener, List<TcpClient> held,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                lock (held)
                {
                    held.Add(client);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException
                                       or ObjectDisposedException)
        {
            // Listener shut down.
        }
    }

    private static ProcessStartInfo BuildStartInfo(string cli, string[] args)
    {
        if (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in new[] { cli }.Concat(args))
            {
                psi.ArgumentList.Add(arg);
            }

            return psi;
        }

        var direct = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            direct.ArgumentList.Add(arg);
        }

        return direct;
    }
}
