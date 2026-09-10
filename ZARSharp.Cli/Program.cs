using ZARSharp.Pipeline;

namespace ZARSharp.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        var exitCode = ZarchiveCli.Run(args, log: Console.WriteLine);
        return exitCode;
    }
}
