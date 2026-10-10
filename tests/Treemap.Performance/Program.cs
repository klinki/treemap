using BenchmarkDotNet.Running;

namespace Treemap.Performance;

internal static class Program
{
    private static void Main(string[] args)
    {
        // --- Safety preamble -------------------------------------------------
        // These benchmarks generate large object graphs. Two guards apply:
        //   1. PerfConfig.MaxFileCount refuses oversized trees outright.
        //   2. PerfConfig sets DOTNET_GCHeapHardLimit on each benchmark child
        //      process, so runaway allocation fails in that process rather than
        //      starving the machine.
        //
        // Default to ShortRun rather than BDN's default job, which can take many
        // minutes per benchmark. Pass --full to opt into the thorough run.
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        if (args.Contains("--list"))
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(["--list", .. args.Where(a => a != "--list")]);
            return;
        }

        PrintEnvironment();

        // Only inject a default job when the caller did not specify one, otherwise
        // BDN rejects the run with a duplicate "--job" option error.
        bool hasJob = args.Any(a =>
            a.Equals("-j", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--job", StringComparison.OrdinalIgnoreCase));

        string[] effective;
        if (args.Contains("--full"))
        {
            effective = [.. args.Where(a => a != "--full")];
        }
        else if (hasJob)
        {
            effective = args;
        }
        else
        {
            effective = ["--job", "short", .. args];
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effective);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Treemap performance benchmarks");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  (no args)              show this help");
        Console.WriteLine("  --list                 list available benchmarks");
        Console.WriteLine("  --filter *Layout*      run matching benchmarks (ShortRun by default)");
        Console.WriteLine("  --full                 use BDN's default thorough job instead of ShortRun");
        Console.WriteLine();
        Console.WriteLine("Add --job dry for a 1-iteration smoke test before a real run.");
        Console.WriteLine($"Tree size is capped at {PerfConfig.MaxFileCount:N0} files.");
        Console.WriteLine();
        PrintEnvironment();
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine($"Runtime:    {Environment.Version}");
        Console.WriteLine($"Processors: {Environment.ProcessorCount} logical");

        long totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        Console.WriteLine($"GC heap available to this process: ~{totalMem} MB");
        Console.WriteLine();
    }
}
