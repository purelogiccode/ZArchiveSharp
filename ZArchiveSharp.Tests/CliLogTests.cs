using System.Reflection;
using System.Text;

namespace ZArchiveSharp.Tests;

/// <summary>
/// In-process checks for the CLI's logging helpers. The CLI is normally
/// exercised as a child process; these load its assembly only to prove the
/// "logging never throws" contract when the console itself is broken.
/// </summary>
public sealed class CliLogTests
{
    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            throw new IOException("broken pipe");
        }

        public override void Write(string? value)
        {
            throw new IOException("broken pipe");
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            throw new IOException("broken pipe");
        }
    }

    [Fact]
    public void Progress_BrokenConsole_DoesNotThrow()
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return;
        }

        var assemblyPath = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? cli
            : Path.Combine(Path.GetDirectoryName(cli)!, "ZArchiveSharp.Cli.dll");
        if (!File.Exists(assemblyPath))
        {
            return;
        }

        var cliLog = Assembly.LoadFrom(assemblyPath).GetType("ZArchiveSharp.Cli.CliLog", throwOnError: true)!;
        var progress = cliLog.GetMethod("Progress", BindingFlags.Public | BindingFlags.Static)!;

        var original = Console.Out;
        try
        {
            Console.SetOut(new ThrowingWriter());
            // Pre-fix the unguarded Console.Write threw IOException (wrapped
            // here in TargetInvocationException) and could abort a pack whose
            // progress callback was running.
            progress.Invoke(null, ["progress line"]);
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
