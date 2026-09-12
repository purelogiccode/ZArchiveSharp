using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ZArchiveSharp.Tests;

/// <summary>
/// Regression test for the update check: it runs in the background and must
/// never delay the command or process exit. A local TCP listener accepts the
/// proxied check and never answers, so a blocking implementation would wait
/// out its full cap while a non-blocking one exits immediately.
/// </summary>
public sealed class UpdateCheckLatencyTests
{
    [Fact]
    public async Task Version_WithHangingUpdateEndpoint_ExitsWithoutWaiting()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        using var stop = new CancellationTokenSource();
        var acceptor = AcceptWhileRunning(listener, held, stop.Token);

        try
        {
            var psi = BuildStartInfo(cli);
            var proxy = $"http://127.0.0.1:{port}";
            psi.Environment["HTTP_PROXY"] = proxy;
            psi.Environment["HTTPS_PROXY"] = proxy;
            psi.Environment["http_proxy"] = proxy;
            psi.Environment["https_proxy"] = proxy;
            psi.Environment.Remove("ZAR_BUG_REPORT");

            var clock = Stopwatch.StartNew();
            using var proc = Process.Start(psi);
            Assert.NotNull(proc);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            var exited = proc.WaitForExit(60_000);
            clock.Stop();

            Assert.True(exited, "The CLI did not exit.");
            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("zar ", stdout, StringComparison.Ordinal);
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(1500),
                $"The CLI waited {clock.Elapsed} on the update check (stderr: {stderr}).");
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

    private static ProcessStartInfo BuildStartInfo(string cli)
    {
        if (cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return new ProcessStartInfo("dotnet", $"\"{cli}\" --version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        return new ProcessStartInfo(cli, "--version")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
    }
}
