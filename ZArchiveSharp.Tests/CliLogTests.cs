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

    private static Type? LoadCliType(string name)
    {
        var cli = RedumpIsoTests.FindCli();
        if (cli is null)
        {
            return null;
        }

        var assemblyPath = cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? cli
            : Path.Combine(Path.GetDirectoryName(cli)!, "ZArchiveSharp.Cli.dll");
        if (!File.Exists(assemblyPath))
        {
            return null;
        }

        return Assembly.LoadFrom(assemblyPath).GetType(name, throwOnError: true);
    }

    [Fact]
    public void Progress_BrokenConsole_DoesNotThrow()
    {
        var cliLog = LoadCliType("ZArchiveSharp.Cli.CliLog");
        if (cliLog is null)
        {
            return;
        }

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

    [Theory]
    [InlineData("C:\\Users\\bob\\game.zar", "bob", "C:\\Users\\bob", "%USERPROFILE%\\game.zar")]
    [InlineData("run application now", "app", "", "run application now")]
    [InlineData("run app now", "app", "", "run [user] now")]
    [InlineData("abacus and ab", "ab", "", "abacus and [user]")]
    [InlineData("u is short", "u", "", "[user] is short")]
    [InlineData("%USERPROFILE%\\x", "userprofile", "", "%USERPROFILE%\\x")]
    public void BugReportFormatter_SanitizeMasksWholeUserName(
        string value, string userName, string profilePath, string expected)
    {
        var formatter = LoadCliType("ZArchiveSharp.Cli.BugReportFormatter");
        if (formatter is null)
        {
            return;
        }

        // Pre-fix usernames shorter than 3 chars were never masked and any
        // 3+ char username was replaced as a plain substring ("app" broke
        // "application"), so this is both an under- and over-redaction fix.
        var method = formatter.GetMethod("Sanitize", BindingFlags.NonPublic | BindingFlags.Static, null,
            [typeof(string), typeof(string), typeof(string)], null);
        Assert.NotNull(method);
        Assert.Equal(expected, (string)method.Invoke(null, [value, userName, profilePath])!);
    }

    [Theory]
    [InlineData("https://github.com/purelogiccode/ZArchiveSharp", true)]
    [InlineData("http://example.com/asset.zip", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void UpdateChecker_ShellOpenAcceptsOnlyWebUrls(string? url, bool expected)
    {
        var updateChecker = LoadCliType("ZArchiveSharp.Cli.UpdateChecker");
        if (updateChecker is null)
        {
            return;
        }

        // Pre-fix OpenBrowser handed whatever the API returned to
        // UseShellExecute; the validator is new.
        var method = updateChecker.GetMethod("IsWebUrl", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Equal(expected, (bool)method.Invoke(null, [url])!);
    }
}