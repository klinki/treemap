using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;

namespace Treemap.Performance;

/// <summary>
/// Shared benchmark configuration: columns and the memory diagnoser, nothing else.
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
/// caps sizes and each benchmark class builds only what it needs, retaining at most two
/// trees at a time.
/// </para>
///
/// <para>
/// <b>No job is declared here.</b> The run job is attached exactly once, globally, by
/// <see cref="Program"/> and marked as the default, so a caller-supplied
/// <c>--job dry</c> or <c>--job short</c> replaces it instead of producing a second job
/// alongside this config.
/// </para>
///
/// <para>
/// <b>No managed heap limit here.</b> Process and job memory are capped by the kernel
/// job object in <c>tools/Invoke-Bounded.ps1</c>, and every benchmark child process
/// inherits those caps. A managed heap limit here could only be looser than the parent
/// cap and would add nothing.
/// </para>
/// </remarks>
public sealed class PerfConfig : ManualConfig
{
    /// <summary>Hard ceiling on generated tree size, in files.</summary>
    public const int MaxFileCount = 100_000;

    public PerfConfig()
    {
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}