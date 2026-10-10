using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;

namespace Treemap.Performance;

/// <summary>
/// Shared benchmark configuration.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately conservative. These benchmarks exercise an interactive UI path, so
/// budgets are sized against a 60fps frame, not peak throughput. A change that makes
/// rendering 3x slower still paints a usable interface; one that makes it 100x slower
/// does not, and that is the regression worth catching.
/// </para>
///
/// <para>
/// <b>Memory:</b> tree generation dominates allocation, so <see cref="MaxFileCount"/>
/// caps sizes and each benchmark class builds only what it needs. An earlier draft
/// materialised 500K/100K/10K/1K-node trees simultaneously in one setup, which is
/// several GB of live <c>TreemapNode</c> objects before BDN even starts iterating.
/// </para>
/// </remarks>
public sealed class PerfConfig : ManualConfig
{
    /// <summary>Hard ceiling on generated tree size, in files.</summary>
    public const int MaxFileCount = 100_000;

    /// <summary>
    /// 2 GiB managed heap ceiling applied to each benchmark child process. Far more
    /// than any benchmark here needs, but it converts a runaway allocation into a
    /// clean OutOfMemoryException in one process instead of exhausting the machine.
    /// </summary>
    internal const string HeapHardLimitHex = "0x80000000";

    public PerfConfig()
    {
        AddJob(Job.Default
            .WithGcServer(false)
            .WithEnvironmentVariable("DOTNET_GCHeapHardLimit", HeapHardLimitHex)
            .WithEnvironmentVariable("DOTNET_gcServer", "0"));

        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(MemoryDiagnoser.Default);
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
