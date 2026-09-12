using System.Runtime.InteropServices;
using System.Text;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Builds bug-report payloads for the PureLogicCode bug-report API (see
/// <c>InstructionsToSendBugs.md</c> in AspNet_BugReportEmailService).
/// Every report carries the three required sections: environment details,
/// error details, and exception details. Field sizes respect the server
/// limits (message 4000, version 20, environment 50, stackTrace 8000).
/// Hand-rolled JSON keeps the CLI trimmable/AOT-compatible.
/// </summary>
internal static class BugReportFormatter
{
    internal const string ApplicationName = "ZArchiveSharp.Cli";

    private const int MaxMessage = 4000;
    private const int MaxStackTrace = 8000;
    private const int MaxVersion = 20;
    private const int MaxEnvironment = 50;

    /// <summary>Full <c>message</c> field: the three required sections.</summary>
    public static string FormatMessage(string error, Exception? ex)
    {
        var environment = BuildEnvironmentSection();
        var stack = ex?.StackTrace ?? "(none)";
        var @fixed = environment + Environment.NewLine + Environment.NewLine
            + "=== Error Details ===" + Environment.NewLine + error + Environment.NewLine + Environment.NewLine
            + "=== Exception Details ===" + Environment.NewLine
            + $"Type: {ex?.GetType().FullName ?? "(none)"}" + Environment.NewLine
            + $"Message: {ex?.Message ?? "(none)"}" + Environment.NewLine
            + $"Source: {ex?.Source ?? "(none)"}" + Environment.NewLine
            + "StackTrace: ";
        var budget = MaxMessage - @fixed.Length - Environment.NewLine.Length;
        if (budget < 0)
        {
            budget = 0;
        }

        var shown = stack.Length > budget ? stack[..budget] + "..." : stack;
        var message = @fixed + shown + Environment.NewLine;
        return message.Length > MaxMessage ? message[..MaxMessage] : message;
    }

    /// <summary><c>stackTrace</c> field: full exception text.</summary>
    public static string FormatStackTrace(Exception? ex)
    {
        return Truncate(ex?.ToString() ?? string.Empty, MaxStackTrace);
    }

    /// <summary><c>version</c> field: assembly version core.</summary>
    public static string FormatVersion()
    {
        return Truncate(Program.GetVersion(), MaxVersion);
    }

    /// <summary><c>environment</c> field: short OS description.</summary>
    public static string FormatEnvironment()
    {
        return Truncate(RuntimeInformation.OSDescription, MaxEnvironment);
    }

    /// <summary>
    /// Strips local-account details before a value leaves the process: the
    /// user profile directory becomes <c>%USERPROFILE%</c> (both slash
    /// directions) and any remaining bare username becomes <c>[user]</c>.
    /// Bug reports must never carry the Windows account name.
    /// </summary>
    public static string Sanitize(string value)
    {
        return Sanitize(
            value,
            Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    internal static string Sanitize(string value, string userName, string profilePath)
    {
        if (value.Length == 0)
        {
            return value;
        }

        const string marker = "%USERPROFILE%";
        if (profilePath.Length > 0)
        {
            value = value.Replace(profilePath, marker, StringComparison.OrdinalIgnoreCase);
            var forward = profilePath.Replace('\\', '/');
            if (!string.Equals(forward, profilePath, StringComparison.Ordinal))
            {
                value = value.Replace(forward, marker, StringComparison.OrdinalIgnoreCase);
            }
        }

        // Usernames as short as "user" would otherwise corrupt the marker
        // (case-insensitive match inside %USERPROFILE%), so mask it first.
        if (userName.Length >= 3)
        {
            value = value.Replace(marker, "\0", StringComparison.Ordinal);
            value = value.Replace(userName, "[user]", StringComparison.OrdinalIgnoreCase);
            value = value.Replace("\0", marker, StringComparison.Ordinal);
        }

        return value;
    }

    /// <summary>Escapes <paramref name="value"/> as a JSON string literal.</summary>
    public static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append(@"\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    private static string BuildEnvironmentSection()
    {
        var sb = new StringBuilder();
        sb.Append("=== Environment Details ===").Append(Environment.NewLine);
        sb.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("o")).Append(Environment.NewLine);
        sb.Append("Application Name: ").Append(ApplicationName).Append(Environment.NewLine);
        sb.Append("Application Version: ").Append(Program.GetVersion()).Append(Environment.NewLine);
        sb.Append("OS Version: ").Append(RuntimeInformation.OSDescription).Append(Environment.NewLine);
        sb.Append("Architecture: ").Append(RuntimeInformation.OSArchitecture).Append(Environment.NewLine);
        sb.Append("Bitness: ").Append(Environment.Is64BitProcess ? "64-bit" : "32-bit").Append(" process / ")
            .Append(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit").Append(" OS").Append(Environment.NewLine);
        sb.Append("Windows Version: ").Append(OperatingSystem.IsWindows()
            ? Environment.OSVersion.VersionString
            : "N/A (non-Windows)").Append(Environment.NewLine);
        sb.Append("Processor Count: ").Append(Environment.ProcessorCount).Append(Environment.NewLine);
        sb.Append("Base Directory: ").Append(AppContext.BaseDirectory).Append(Environment.NewLine);
        sb.Append("Temp Path: ").Append(Path.GetTempPath());
        return sb.ToString();
    }

    private static string Truncate(string value, int max)
    {
        return value.Length > max ? value[..max] : value;
    }
}
