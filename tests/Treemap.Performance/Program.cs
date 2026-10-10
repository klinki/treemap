using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;

namespace Treemap.Performance;

internal static class Program
{
    /// <summary>BDN requires a value for <c>--list</c>; these are the accepted ones.</summary>
    private static readonly string[] ListForms = ["flat", "tree"];

    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        // Exactly one job, attached globally and marked as the default. Marking it
        // default is what makes a caller-supplied --job an override rather than an
        // extra job sitting next to the class-level PerfConfig.
        bool full = args.Contains("--full", StringComparer.Ordinal);
        Job job = full
            ? Job.Default.WithGcServer(false).AsDefault()
            : Job.ShortRun.WithGcServer(false).AsDefault();

        ManualConfig config = ManualConfig.Create(DefaultConfig.Instance);
        config.AddJob(job);

        // --list still runs through the same config so the listing matches the run.
        if (HasListFlag(args))
        {
            BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly)
                .Run(NormalizeListArguments(args.Where(a => a != "--full").ToArray()), config);
            return;
        }

        PrintEnvironment();

        // --full only selects the job above, so BDN never sees it.
        string[] effective = full
            ? [.. args.Where(a => !a.Equals("--full", StringComparison.Ordinal))]
            : args;

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(effective, config);
    }

    private static bool HasListFlag(string[] args)
        => args.Any(a => a.Equals("--list", StringComparison.Ordinal)
                     || a.StartsWith("--list=", StringComparison.Ordinal));

    /// <summary>
    /// BDN rejects a bare <c>--list</c> because it expects a form. Supplying
    /// <c>flat</c> or <c>tree</c> is left exactly as given; a lone <c>--list</c> becomes
    /// <c>--list flat</c>, which is the form that suits scripts and CI logs.
    /// </summary>
    private static string[] NormalizeListArguments(string[] args)
    {
        List<string> normalized = new(args.Length + 1);

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--list=", StringComparison.Ordinal))
            {
                // Already carries an explicit form; leave it untouched.
                normalized.Add(args[i]);
                continue;
            }

            if (!args[i].Equals("--list", StringComparison.Ordinal))
            {
                normalized.Add(args[i]);
                continue;
            }

            string? next = i + 1 < args.Length ? args[i + 1] : null;
            if (next is not null && ListForms.Contains(next, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add("--list");
                normalized.Add(next.ToLowerInvariant());
                i++;
            }
            else
            {
                normalized.Add("--list");
                normalized.Add("flat");
            }
        }

        return [.. normalized];
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Treemap performance benchmarks");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  (no args)              show this help");
        Console.WriteLine("  --list [flat|tree]     list available benchmarks (defaults to flat)");
        Console.WriteLine("  --filter *Layout*      run matching benchmarks (ShortRun by default)");
        Console.WriteLine("  --full                 use BDN's default thorough job instead of ShortRun");
        Console.WriteLine();
        Console.WriteLine("Add --job dry for a 1-iteration smoke test before a real run; it");
        Console.WriteLine("overrides the default job rather than adding a second one.");
        Console.WriteLine($"Tree size is capped at {PerfConfig.MaxFileCount:N0} files.");
        Console.WriteLine();
        Console.WriteLine("Run this through tools/Invoke-Bounded.ps1: process and job memory caps");
        Console.WriteLine("are enforced by the kernel and inherited by every child process.");
        Console.WriteLine();
        PrintEnvironment();
    }

    private static void PrintEnvironment()
    {
        Console.WriteLine($"Runtime:    {Environment.Version}");
        Console.WriteLine($"Processors: {Environment.ProcessorCount} logical");

        long totalMem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
        Console.WriteLine($"GC heap available to this process: ~{totalMem} MB");

        string? heapLimit = Environment.GetEnvironmentVariable("DOTNET_GCHeapHardLimit");
        Console.WriteLine(
            $"DOTNET_GCHeapHardLimit: {(string.IsNullOrEmpty(heapLimit) ? "not set" : "0x" + heapLimit)}");
        Console.WriteLine(
            "Process/job memory caps are kernel-enforced and are not visible from managed code.");
        Console.WriteLine();
    }
}