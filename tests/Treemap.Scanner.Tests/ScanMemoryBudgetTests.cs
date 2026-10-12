using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

public class ScanMemoryBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveMemoryBudgetIsRejected(long bytes)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new ScanOptions { MemoryBudgetBytes = bytes }.Validate());

    [Fact]
    public async Task MemoryLimitReturnsUsefulPartialDataAndStableIdsOnRescan()
    {
        using TempTree tree = new(nameof(MemoryLimitReturnsUsefulPartialDataAndStableIdsOnRescan));
        ScriptedBackend backend = Files();
        FileSystemScanner scanner = new(backend, null);
        SnapshotPublisher publisher = TestPublisher.Empty();
        ScanOptions options = Options(16_384);

        ScanResult first = await scanner.ScanAsync(tree.Root, publisher, options);

        Assert.False(first.IsComplete);
        Assert.Contains("memory budget", first.IncompleteReason!, StringComparison.Ordinal);
        Assert.InRange(first.FileCount, 1, 99);
        Assert.Equal(first.FileCount, first.Root.Children.Count);
        Assert.Same(first.Root, publisher.Current.Root);
        Assert.Equal(100 * first.FileCount, first.TotalLogicalSize);

        ScanResult second = await scanner.ScanAsync(tree.Root, options: Options(32_768));
        Assert.True(second.FileCount > first.FileCount);
        Assert.Equal(first.Root.Children[0].Id, second.Root.Children[0].Id);
    }

    [Fact]
    public async Task LongPathsAndInferredParentsConsumeMoreBudget()
    {
        using TempTree tree = new(nameof(LongPathsAndInferredParentsConsumeMoreBudget));
        string prefix = Path.Combine(Enumerable.Repeat(new string('x', 100), 5).ToArray());
        // Sized between the flat total (~27KB) and the deep total (~31KB) under the
        // current table, so the flat scan completes while the deep one stops early.
        ScanResult flat = await new FileSystemScanner(Files(), null).ScanAsync(tree.Root, options: Options(28_000));
        ScanResult deep = await new FileSystemScanner(Files(prefix), null).ScanAsync(tree.Root, options: Options(28_000));

        Assert.InRange(deep.FileCount, 1, flat.FileCount - 1);
        Assert.Contains("memory budget", deep.IncompleteReason!, StringComparison.Ordinal);
        Assert.Equal(NodeKind.Folder, Assert.Single(deep.Root.Children).Kind);
        Assert.Equal(100 * deep.FileCount, deep.TotalLogicalSize);
    }

    [Fact]
    public async Task LongRootPathsLeaveLessRoomForQueuedFolders()
    {
        using TempTree tree = new(nameof(LongRootPathsLeaveLessRoomForQueuedFolders));
        ScriptedBackend folders = new(ScanBackend.Portable);
        for (int i = 0; i < 100; i++)
        {
            folders.Entries.Add(new ScanEntry($"d{i:D3}", NodeKind.Folder, 0, 0, null));
        }

        string deepRoot = Path.Combine(tree.Root, Path.Combine(Enumerable.Repeat(new string('x', 100), 20).ToArray()));
        FileSystemScanner scanner = new(folders, null);
        ScanResult shortRoot = await scanner.ScanAsync(tree.Root, options: Options(16_384));
        ScanResult longRoot = await scanner.ScanAsync(deepRoot, options: Options(16_384));

        Assert.InRange(longRoot.DirectoryCount, 1, shortRoot.DirectoryCount - 1);
        Assert.Contains("memory budget", longRoot.IncompleteReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FallbackDiscardsTheAbandonedMemoryCharge()
    {
        using TempTree tree = new(nameof(FallbackDiscardsTheAbandonedMemoryCharge));
        ScriptedBackend fast = Files(id: ScanBackend.NtfsMft);
        fast.Succeeds = false;
        fast.FailureReason = "unreadable MFT";
        ScriptedBackend portable = new(ScanBackend.Portable);
        portable.Entries.Add(new ScanEntry("real.txt", NodeKind.File, 100, 100, null));

        ScanResult result = await new FileSystemScanner(portable, fast).ScanAsync(
            tree.Root, options: Options(4096) with { Backend = ScanBackendPreference.Auto });

        Assert.True(result.IsComplete);
        Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);
        Assert.Equal("real.txt", Assert.Single(result.Root.Children).Name);
        Assert.Equal(100, result.TotalLogicalSize);
    }

    [Fact]
    public void DiagnosticStorageIsChargedAndMemoryReasonRemainsVisible()
    {
        ScanCollector collector = new(Options(16_384), null);
        collector.AddDiagnostic("denied", "access denied");
        for (int i = 0; i < 100 && !collector.ShouldStop(); i++)
        {
            collector.AddDiagnostic("denied", new string('x', 4096));
        }

        Assert.True(collector.ShouldStop());
        Assert.Contains("memory budget", collector.IncompleteReason!, StringComparison.Ordinal);
        Assert.InRange(collector.Diagnostics.Count, 1, 3);
        Assert.True(collector.SuppressedDiagnostics > 0);
    }

    [Fact]
    public async Task CancellationAtTheMemoryLimitedBuildStillPreservesThePriorMap()
    {
        using TempTree tree = new(nameof(CancellationAtTheMemoryLimitedBuildStillPreservesThePriorMap));
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileSystemScanner(Files(), null).ScanAsync(
            tree.Root, publisher, Options(16_384), new CancellingProgress(cancellation, 1), cancellation.Token));

        Assert.Same(prior, publisher.Current);
    }

    [WindowsOnlyFact]
    public async Task MemoryLimitedFinalMapKeepsThePreviewAndUnaccountedSpace()
    {
        string volume = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        SnapshotPublisher publisher = TestPublisher.Empty();
        TaskCompletionSource<int> preview = new(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Published += () =>
        {
            TreemapNode? first = TreeAssert.Find(publisher.Current.Root, "first.bin");
            if (first is not null)
            {
                preview.TrySetResult(first.Id);
            }
        };

        FileSystemScanner scanner = new(new PreviewBackend(entered, release), null, _ => new VolumeSpaceInfo(100_000, 20_000));
        Task<ScanResult> scan = scanner.ScanAsync(volume, publisher, Options(4096) with
        {
            PublishIntermediateSnapshots = true,
            SnapshotInterval = TimeSpan.FromMilliseconds(10),
        });

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            int previewId = await preview.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            ScanResult result = await scan.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.IsComplete);
            Assert.Contains("memory budget", result.IncompleteReason!, StringComparison.Ordinal);
            Assert.Equal(previewId, TreeAssert.Find(result.Root, "first.bin")!.Id);
            Assert.Same(result.Root, publisher.Current.Root);
            Assert.Equal(20_000, result.FreeSpaceBytes);
            Assert.Equal(80_000 - result.TotalPhysicalSize, result.UnknownSpaceBytes);
            Assert.Contains(result.Root.Children, child => child.Kind == NodeKind.UnknownSpace);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public void MeasuredCInventoryFitsTheAutomaticCap()
    {
        // Independent streaming count: 2,927,443 files + 615,160 folders at ~27
        // chars average basename. The retained estimate (plus per-folder queued
        // absolute-path workspace for a short C:\ root) must fit the automatic
        // cap, or a full C scan stops on budget before enumeration ends.
        const long Files = 2_927_443;
        const long Folders = 615_160;
        const int AvgName = 27;
        long total = Files * ScanMemoryBudget.EstimateFile(AvgName)
            + Folders * ScanMemoryBudget.EstimateFolder(AvgName)
            + Folders * 2L * 3;
        Assert.True(
            total <= ScanMemoryBudget.MaxAutomaticBytes,
            $"C-scale estimate {total:N0} bytes exceeds the {ScanMemoryBudget.MaxAutomaticBytes:N0} byte automatic cap.");
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(999_999, 500)]
    [InlineData(1_000_000, 2000)]
    [InlineData(2_000_000, 3000)]
    [InlineData(5_000_000, 3000)]
    public void LargeTreesSlowPreviewsButSmallTreesKeepCallerInterval(long entries, int expectedMs)
    {
        TimeSpan requested = TimeSpan.FromMilliseconds(500);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), ScanMemoryBudget.PreviewDelay(requested, entries));
    }

    [Fact]
    public void LargerCallerPreviewIntervalIsAlwaysHonoured()
    {
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            ScanMemoryBudget.PreviewDelay(TimeSpan.FromSeconds(10), 5_000_000));
        Assert.Equal(
            TimeSpan.FromMilliseconds(10),
            ScanMemoryBudget.PreviewDelay(TimeSpan.FromMilliseconds(10), 0));
    }

    private static ScanOptions Options(long bytes) => TestOptions.Verbose() with { MemoryBudgetBytes = bytes };

    private static ScriptedBackend Files(string prefix = "", ScanBackend id = ScanBackend.Portable)
    {
        ScriptedBackend backend = new(id);
        for (int i = 0; i < 100; i++)
        {
            backend.Entries.Add(new ScanEntry(Path.Combine(prefix, $"f{i:D3}.bin"), NodeKind.File, 100, 100, null));
        }

        return backend;
    }

    private sealed class PreviewBackend(ManualResetEventSlim entered, ManualResetEventSlim release) : IScanBackend
    {
        public ScanBackend Id => ScanBackend.Portable;

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            if (!collector.TryAdd("first.bin", NodeKind.File, 100, 100, null))
            {
                return true;
            }

            entered.Set();
            release.Wait(request.CancellationToken);
            for (int i = 0; i < 100; i++)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (!collector.TryAdd($"later{i:D3}.bin", NodeKind.File, 100, 100, null))
                {
                    break;
                }
            }

            return true;
        }
    }
}
