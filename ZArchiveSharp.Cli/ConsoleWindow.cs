using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZArchiveSharp.Cli;

/// <summary>
/// Keeps the console window open when the CLI was launched by a double-click.
/// Explorer gives the process its own console (only this process is attached),
/// so the window would vanish before the output can be read, while a
/// shell-launched process shares its terminal's console and is never held.
/// Windows-only; quiet runs, redirected input, debugger sessions and terminal
/// launches skip it. CLI binary project only.
/// </summary>
internal static class ConsoleWindow
{
    /// <summary>
    /// Waits for a key press when this process owns its console window (the
    /// double-click case). Never throws and never alters the exit code.
    /// </summary>
    internal static void KeepOpenIfOwned(bool quiet)
    {
        if (quiet || !OwnsConsole())
        {
            return;
        }

        try
        {
            Console.Error.WriteLine();
            Console.Error.Write("Press any key to exit . . .");
            Console.ReadKey(intercept: true);
            Console.Error.WriteLine();
        }
        catch (Exception holdEx)
        {
            // A broken or redirected console must never change the exit code.
            _ = holdEx;
        }
    }

    private static bool OwnsConsole()
    {
        if (!OperatingSystem.IsWindows() || Debugger.IsAttached || Console.IsInputRedirected)
        {
            return false;
        }

        try
        {
            // One process attached (this one) means nobody will keep the
            // window alive after we exit, i.e. an Explorer launch.
            var processes = new uint[1];
            return GetConsoleProcessList(processes, 1) == 1;
        }
        catch (Exception consoleEx)
        {
            _ = consoleEx;
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);
}