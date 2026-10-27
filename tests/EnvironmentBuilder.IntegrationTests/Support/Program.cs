using System.Text;

namespace EnvironmentBuilder.IntegrationTests.Support;

// The adapter test copies this apphost as docker.exe. It emits bytes only and has no Docker/HTTP behavior.
internal static class Program
{
    public const string ProcessSentinel = "adapter-secret+/canary";
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--host" && args[2] == "process-contract-fixture")
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            await Task.WhenAll(WriteAsync(Console.Out, "stdout"), WriteAsync(Console.Error, "stderr"));
            return 0;
        }
        return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);
    }
    private static async Task WriteAsync(TextWriter stream, string label)
    {
        await stream.WriteAsync(label + " " + ProcessSentinel[..8]); await stream.FlushAsync();
        await stream.WriteAsync(ProcessSentinel[8..] + "\n");
        string chunk = new('x', 4096);
        for (int i = 0; i < 1100; i++) { await stream.WriteAsync(chunk); }
        await stream.FlushAsync();
    }
}
