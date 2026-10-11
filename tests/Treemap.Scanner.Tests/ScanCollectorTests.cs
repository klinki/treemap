using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// The shared budget. Several workers draw on one collector, so this is the single place
/// where an unbounded scan would become an unbounded result - it is tested directly.
/// </summary>
public class ScanCollectorTests
{
    private const long Budget = 40;

    /// <summary>
    /// Files, folders and diagnostics all cost the same unit, from several threads at
    /// once. Whatever the interleaving, the total recorded cannot pass the budget and
    /// cannot fall short of it either while work is still being offered.
    /// </summary>
    [Fact]
    public void ConcurrentAddsAndDiagnosticsCannotOvershootTheBudget()
    {
        ScanCollector collector = new(
            ScanOptions.Default with { MaxEntries = Budget, MaxDiagnostics = int.MaxValue },
            progress: null);

        int accepted = 0;

        Parallel.For(0, ScanOptions.MaxParallelismHardLimit, _ =>
        {
            for (int i = 0; i < 100; i++)
            {
                switch (i % 4)
                {
                    case 0:
                        if (collector.TryAdd($"file{i}.bin", NodeKind.File, 10, 10, null))
                        {
                            Interlocked.Increment(ref accepted);
                        }

                        break;

                    case 1:
                        if (collector.TryAdd($"dir{i}", NodeKind.Folder, 0, 0, null))
                        {
                            Interlocked.Increment(ref accepted);
                        }

                        break;

                    case 2:
                        collector.AddDiagnostic($"path{i}", "unreadable");
                        break;

                    default:
                        collector.AddDiagnostic($"path{i}", "not followed", ScanDiagnosticSeverity.Warning);
                        break;
                }
            }
        });

        int recorded = collector.Entries.Count + collector.Diagnostics.Count;

        Assert.Equal(Budget, recorded);
        Assert.Equal(accepted, collector.Entries.Count);
        Assert.Equal(collector.FileCount + collector.DirectoryCount, collector.Entries.Count);
        Assert.False(collector.IsComplete);
        Assert.NotNull(collector.IncompleteReason);
    }

    [Fact]
    public void TryAddReportsFailureOnceTheBudgetIsSpent()
    {
        ScanCollector collector = new(ScanOptions.Default with { MaxEntries = 3 }, progress: null);

        Assert.True(collector.TryAdd("a", NodeKind.File, 1, 1, null));
        Assert.True(collector.TryAdd("b", NodeKind.File, 1, 1, null));
        Assert.True(collector.TryAdd("c", NodeKind.File, 1, 1, null));
        Assert.False(collector.TryAdd("d", NodeKind.File, 1, 1, null));

        // The rejected entry cost nothing and left nothing behind.
        Assert.Equal(3, collector.Entries.Count);
        Assert.Equal(3, collector.LogicalBytes);
    }

    /// <summary>
    /// A diagnostic flood still spends the budget, so producing many of them cannot push
    /// real entries out of the result.
    /// </summary>
    [Fact]
    public void DiagnosticsAreChargedToTheSameBudget()
    {
        ScanCollector collector = new(
            ScanOptions.Default with { MaxEntries = 10, MaxDiagnostics = 2 },
            progress: null);

        for (int i = 0; i < 6; i++)
        {
            collector.AddDiagnostic($"path{i}", "unreadable");
        }

        Assert.True(collector.TryAdd("kept.bin", NodeKind.File, 1, 1, null));
        Assert.Single(collector.Entries);
        Assert.True(collector.SuppressedDiagnostics > 0);
        Assert.False(collector.IsComplete);
    }

    /// <summary>
    /// An extra name for a hard-linked file counts as a link, while the bytes it
    /// contributes are charged exactly as any other entry's would be.
    /// </summary>
    [Fact]
    public void ExtraHardLinkNamesAreCountedSeparatelyFromTheirSizes()
    {
        ScanCollector collector = new(ScanOptions.Default, progress: null);

        collector.TryAdd(Path.Combine("one", "shared.bin"), NodeKind.File, 500, 500, null);
        collector.TryAdd(Path.Combine("two", "shared.bin"), NodeKind.File, 500, 500, null, isExtraHardLinkEntry: true);

        Assert.Equal(2, collector.FileCount);
        Assert.Equal(1, collector.HardLinkedFileCount);
        Assert.Equal(1000, collector.LogicalBytes);
    }

    /// <summary>
    /// Not being able to read allocated size is a platform limit, not a failure, and it
    /// is worth saying once rather than once per file.
    /// </summary>
    [Fact]
    public void UnavailableAllocationIsReportedOnceAndDoesNotFailTheScan()
    {
        ScanCollector collector = new(ScanOptions.Default, progress: null);

        collector.NoteAllocationUnavailable("no allocation here");
        collector.NoteAllocationUnavailable("no allocation here either");

        Assert.Single(collector.Diagnostics);
        Assert.True(collector.IsComplete);
    }
}

/// <summary>
/// Which exceptions may be turned into a diagnostic or a fallback. Both backends decide
/// that with this one predicate, so the exclusion of cancellation and of fatal faults is
/// worth pinning down here.
/// </summary>
public class ScanFailureTests
{
    [Fact]
    public void FilesystemProblemsAreRecoverable()
    {
        Assert.True(ScanFailure.IsRecoverable(new IOException("locked")));
        Assert.True(ScanFailure.IsRecoverable(new UnauthorizedAccessException()));
        Assert.True(ScanFailure.IsRecoverable(new DirectoryNotFoundException()));
    }

    /// <summary>
    /// A cancelled scan must reach the caller as a cancellation. Recovering from it would
    /// restart the scan on another backend, and recovering from a fault would hide a bug.
    /// </summary>
    [Fact]
    public void CancellationAndFatalFaultsAreNotRecoverable()
    {
        Assert.False(ScanFailure.IsRecoverable(new OperationCanceledException()));
        Assert.False(ScanFailure.IsRecoverable(new OutOfMemoryException()));
        Assert.False(ScanFailure.IsRecoverable(new InvalidOperationException()));
    }
}
