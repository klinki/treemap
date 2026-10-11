using System.Diagnostics;
using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Lazy child storage: flat files build correctly and stay inside a budget that the
/// old per-file collections would break.
/// </summary>
public class ScanLazyStorageTests
{
    [Fact]
    public void EmptyEntryListBuildsAnEmptyRoot()
    {
        string rootPath = FakeRoot(nameof(EmptyEntryListBuildsAnEmptyRoot));
        ScanIdMap ids = new();
        ids.BeginBuild(rootPath);

        TreemapNode root = ScanTreeBuilder.Build(rootPath, [], ids, CancellationToken.None);

        Assert.Empty(root.Children);
        Assert.Equal(0, root.LogicalSize);
        Assert.Equal(0, root.PhysicalSize);
    }

    [Fact]
    public void MixedFilesFoldersAndEmptyFilesAggregate()
    {
        string rootPath = FakeRoot(nameof(MixedFilesFoldersAndEmptyFilesAggregate));
        ScanIdMap ids = new();
        ids.BeginBuild(rootPath);

        TreemapNode root = ScanTreeBuilder.Build(
            rootPath,
            [
                new ScanEntry(Path.Combine("docs", "a.txt"), NodeKind.File, 100, 100, null),
                new ScanEntry(Path.Combine("docs", "empty.txt"), NodeKind.File, 0, 0, null),
                new ScanEntry(Path.Combine("docs", "nested", "deep.bin"), NodeKind.File, 50, 60, null),
                new ScanEntry("loose.txt", NodeKind.File, 25, 25, null),
                new ScanEntry("docs", NodeKind.Folder, 0, 0, null),
            ],
            ids,
            CancellationToken.None);

        Assert.Equal(new[] { "docs", "loose.txt" }, root.Children.Select(c => c.Name));

        TreemapNode docs = TreeAssert.Find(root, "docs")!;
        Assert.Equal(NodeKind.Folder, docs.Kind);
        Assert.Equal(150, docs.LogicalSize);
        Assert.Equal(160, docs.PhysicalSize);
        Assert.Equal(175, root.LogicalSize);

        TreemapNode empty = TreeAssert.Find(root, "docs", "empty.txt")!;
        Assert.Equal(NodeKind.File, empty.Kind);
        Assert.Equal(0, empty.LayoutSize);

        TreemapNode nested = TreeAssert.Find(root, "docs", "nested")!;
        Assert.Equal(NodeKind.Folder, nested.Kind);
        Assert.Equal(TreeAssert.Find(root, "docs", "nested", "deep.bin")!.Id, nested.Children.Single().Id);
    }

    /// <summary>10,000 flat files must build inside 5 MB.</summary>
    [Fact]
    public void BuildingTenThousandFlatFilesStaysWithinBudget()
    {
        const int Count = 10_000;
        // Measured at 4.31 MB; eager per-file collections raise it to 5.43 MB.
        const long BudgetBytes = 5_000_000;
        ScanEntry[] entries = new ScanEntry[Count];
        for (int i = 0; i < Count; i++)
        {
            entries[i] = new ScanEntry($"file-{i:00000}.bin", NodeKind.File, 100, 100, null);
        }

        string rootPath = FakeRoot(nameof(BuildingTenThousandFlatFilesStaysWithinBudget));

        // Warm up JIT so the measured region is steady-state building.
        Assert.Equal(Count, Build(entries, rootPath).Children.Count);

        long before = GC.GetAllocatedBytesForCurrentThread();
        TreemapNode built = Build(entries, rootPath);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Count, built.Children.Count);
        Assert.True(
            allocated < BudgetBytes,
            $"Building {Count:N0} files allocated {allocated:N0} bytes, over the {BudgetBytes:N0} byte budget.");
    }

    private static TreemapNode Build(ScanEntry[] entries, string rootPath)
    {
        ScanIdMap ids = new();
        ids.BeginBuild(rootPath);
        return ScanTreeBuilder.Build(rootPath, entries, ids, CancellationToken.None);
    }

    private static string FakeRoot(string name)
        => Path.Combine(Path.GetTempPath(), "treemap-lazy-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
}

/// <summary>
/// Root-scoped identity: preview, final and rescan share ids for one root, an aborted
/// scan of another root keeps the committed map, and separator/case aliases agree.
/// </summary>
public class ScanScopedIdentityTests
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ScanOptions PreviewOptions() => new()
    {
        Backend = ScanBackendPreference.Portable,
        ProgressInterval = TimeSpan.Zero,
        PublishIntermediateSnapshots = true,
        SnapshotInterval = PreviewInterval,
    };

    private static string FakeRoot(string name)
        => Path.Combine(Path.GetTempPath(), "treemap-scoped-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task DirectoryIdsSurvivePreviewFinalAndRescan()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        BlockingBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry(Path.Combine("sub", "inner.txt"), NodeKind.File, 100, 100, null));
        backend.Entries.Add(new ScanEntry("top.txt", NodeKind.File, 50, 50, null));

        string root = FakeRoot(nameof(DirectoryIdsSurvivePreviewFinalAndRescan));
        FileSystemScanner scanner = new(backend, null);
        Task<ScanResult> scan = scanner.ScanAsync(root, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForNodeAsync(publisher, "sub");
            int previewFolderId = TreeAssert.Find(preview.Root, "sub")!.Id;
            int previewFileId = TreeAssert.Find(preview.Root, "sub", "inner.txt")!.Id;

            backend.Release.Set();
            ScanResult result = await scan;

            // The inferred folder keeps its id from the preview into the final tree.
            Assert.Equal(previewFolderId, TreeAssert.Find(result.Root, "sub")!.Id);
            Assert.Equal(previewFileId, TreeAssert.Find(result.Root, "sub", "inner.txt")!.Id);

            // And into the next rescan, which replays the same entries.
            ScanResult second = await scanner.ScanAsync(root, options: TestOptions.Verbose());
            Assert.Equal(previewFolderId, TreeAssert.Find(second.Root, "sub")!.Id);
            Assert.Equal(previewFileId, TreeAssert.Find(second.Root, "sub", "inner.txt")!.Id);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task AbortedRescanOfAnotherRootKeepsCommittedIds()
    {
        string rootA = FakeRoot(nameof(AbortedRescanOfAnotherRootKeepsCommittedIds) + "-A");
        string rootB = FakeRoot(nameof(AbortedRescanOfAnotherRootKeepsCommittedIds) + "-B");
        RootAwareBackend backend = new() { IsSecondRoot = path => path.Contains("-B-", StringComparison.Ordinal) };
        FileSystemScanner scanner = new(backend, null);

        ScanResult first = await scanner.ScanAsync(rootA, options: TestOptions.Verbose(ScanBackendPreference.Portable));
        int committedIdA = TreeAssert.Find(first.Root, "common.txt")!.Id;

        SnapshotPublisher publisher = TestPublisher.Empty();
        using CancellationTokenSource cts = new();
        Task<ScanResult> doomed = scanner.ScanAsync(
            rootB, publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.EnteredSecond.Wait(Timeout), "The second root never started.");
            TreemapSnapshot preview = await WaitForNodeAsync(publisher, "common.txt");
            int ghostIdB = TreeAssert.Find(preview.Root, "common.txt")!.Id;

            // A different root never reuses the committed root's ids, even for the
            // same relative name.
            Assert.NotEqual(committedIdA, ghostIdB);

            cts.Cancel();
            backend.ReleaseSecond.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);
        }
        finally
        {
            backend.ReleaseSecond.Set();
        }

        // The abort dropped the second root's build: the first root's committed ids
        // are exactly where they were.
        ScanResult rescannedA = await scanner.ScanAsync(rootA, options: TestOptions.Verbose(ScanBackendPreference.Portable));
        Assert.Equal(committedIdA, TreeAssert.Find(rescannedA.Root, "common.txt")!.Id);

        // The second root then completes on fresh ids that stay put on its own rescan.
        ScanResult completedB = await scanner.ScanAsync(rootB, options: TestOptions.Verbose(ScanBackendPreference.Portable));
        int committedIdB = TreeAssert.Find(completedB.Root, "common.txt")!.Id;
        Assert.NotEqual(committedIdA, committedIdB);

        ScanResult rescannedB = await scanner.ScanAsync(rootB, options: TestOptions.Verbose(ScanBackendPreference.Portable));
        Assert.Equal(committedIdB, TreeAssert.Find(rescannedB.Root, "common.txt")!.Id);
    }

    [WindowsOnlyFact]
    public void SeparatorAliasesShareDirectoryIds()
    {
        string root = FakeRoot(nameof(SeparatorAliasesShareDirectoryIds));
        ScanIdMap ids = new();

        ids.BeginBuild(root);
        TreemapNode first = ScanTreeBuilder.Build(
            root,
            [new ScanEntry("a/b.txt", NodeKind.File, 10, 10, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        ids.BeginBuild(root);
        TreemapNode second = ScanTreeBuilder.Build(
            root,
            [new ScanEntry("a\\b.txt", NodeKind.File, 10, 10, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        Assert.Equal(
            TreeAssert.Find(first, "a")!.Id,
            TreeAssert.Find(second, "a")!.Id);
        Assert.Equal(
            TreeAssert.Find(first, "a", "b.txt")!.Id,
            TreeAssert.Find(second, "a", "b.txt")!.Id);
    }

    [WindowsOnlyFact]
    public void CaseAliasesShareDirectoryIds()
    {
        string root = FakeRoot(nameof(CaseAliasesShareDirectoryIds));
        ScanIdMap ids = new();

        ids.BeginBuild(root);
        TreemapNode first = ScanTreeBuilder.Build(
            root,
            [new ScanEntry(Path.Combine("Mixed", "x.txt"), NodeKind.File, 10, 10, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        ids.BeginBuild(root);
        TreemapNode second = ScanTreeBuilder.Build(
            root,
            [new ScanEntry(Path.Combine("mixed", "X.TXT"), NodeKind.File, 10, 10, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        Assert.Equal(
            TreeAssert.Find(first, "Mixed")!.Id,
            TreeAssert.Find(second, "mixed")!.Id);
    }

    [UnixOnlyFact]
    public void BackslashInAUnixFileNameKeepsOneStableId()
    {
        string root = FakeRoot(nameof(BackslashInAUnixFileNameKeepsOneStableId));
        ScanIdMap ids = new();

        ids.BeginBuild(root);
        TreemapNode first = ScanTreeBuilder.Build(
            root,
            [new ScanEntry("weird\\name.txt", NodeKind.File, 7, 7, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        ids.BeginBuild(root);
        TreemapNode second = ScanTreeBuilder.Build(
            root,
            [new ScanEntry("weird\\name.txt", NodeKind.File, 7, 7, null)],
            ids,
            CancellationToken.None);
        ids.Commit();

        TreemapNode file = Assert.Single(second.Children);
        Assert.Equal("weird\\name.txt", file.Name);
        Assert.Equal(
            Assert.Single(first.Children).Id,
            file.Id);
    }

    [Fact]
    public void ReservingRelativeOutsideAScopedBuildIsAProgrammerError()
    {
        ScanIdMap map = new();
        Assert.Throws<InvalidOperationException>(() => map.ReserveRelative("a.txt"));

        map.BeginBuild();
        Assert.Throws<InvalidOperationException>(() => map.ReserveRelative("a.txt"));
    }

    private static async Task<TreemapSnapshot> WaitForNodeAsync(SnapshotPublisher publisher, string name)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            TreemapSnapshot current = publisher.Current;
            if (TreeAssert.Find(current.Root, name) is not null)
            {
                return current;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"No preview containing '{name}' arrived before the timeout.");
    }

    /// <summary>
    /// Adds entries, then waits for release, so previews have a window to fire
    /// mid-scan. Replays the same entries on the next scan once released.
    /// </summary>
    private sealed class BlockingBackend(ScanBackend id) : IScanBackend
    {
        public ScanBackend Id { get; } = id;

        public List<ScanEntry> Entries { get; } = [];

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            request.CancellationToken.ThrowIfCancellationRequested();

            foreach (ScanEntry entry in Entries)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (!collector.TryAdd(entry.RelativePath, entry.Kind, entry.LogicalSize, entry.PhysicalSize, entry.LastWriteTime))
                {
                    break;
                }
            }

            Entered.Set();
            Release.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    /// <summary>
    /// Serves one file for the first root and blocks mid-scan for the second, so an
    /// aborted scan of another root can be staged without touching the filesystem.
    /// </summary>
    private sealed class RootAwareBackend : IScanBackend
    {
        public ScanBackend Id => ScanBackend.Portable;

        public Func<string, bool>? IsSecondRoot { get; set; }

        public ManualResetEventSlim EnteredSecond { get; } = new(false);

        public ManualResetEventSlim ReleaseSecond { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            request.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(collector.TryAdd("common.txt", NodeKind.File, 100, 100, null));

            if (IsSecondRoot?.Invoke(request.RootPath) == true)
            {
                EnteredSecond.Set();
                ReleaseSecond.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
                request.CancellationToken.ThrowIfCancellationRequested();
            }

            return true;
        }
    }
}

/// <summary>
/// Live backend status on progress ticks: the active backend is announced when its
/// attempt begins, and a fallback is carried from the transition, not just the result.
/// </summary>
public class ScanLiveBackendTests
{
    [Fact]
    public async Task TicksCarryActiveBackendAndFallbackAcrossFallback()
    {
        using TempTree tree = new(nameof(TicksCarryActiveBackendAndFallbackAcrossFallback));
        tree.MakeFile("real.txt", 10);

        ScriptedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "MFT unreadable",
        };
        fast.Entries.Add(new ScanEntry("partial-from-mft.txt", NodeKind.File, 5000, 5000, null));

        ScriptedBackend portable = new(ScanBackend.Portable);
        portable.Entries.Add(new ScanEntry("real.txt", NodeKind.File, 100, 100, null));

        RecordingProgress progress = new();
        ScanResult result = await new FileSystemScanner(portable, fast)
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Auto), progress: progress);

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);

        ScanProgress[] ticks = [.. progress.Ticks];

        // The fast attempt announces itself before any portable tick can appear.
        Assert.Contains(ticks, t => t.ActiveBackend == ScanBackend.NtfsMft);

        // From the transition on, ticks carry the retry and why it happened.
        Assert.Contains(
            ticks,
            t => t is { ActiveBackend: ScanBackend.Portable, FallbackFrom: ScanBackend.NtfsMft }
                && t.FallbackReason == "MFT unreadable");

        ScanProgress last = ticks[^1];
        Assert.Equal(ScanPhase.Finished, last.Phase);
        Assert.Equal(ScanBackend.Portable, last.ActiveBackend);
        Assert.Equal(ScanBackend.NtfsMft, last.FallbackFrom);
        Assert.Equal("MFT unreadable", last.FallbackReason);
    }

    [Fact]
    public async Task PortableOnlyScanReportsPortableWithNoFallback()
    {
        using TempTree tree = new(nameof(PortableOnlyScanReportsPortableWithNoFallback));
        tree.MakeFile("a.txt", 10);

        RecordingProgress progress = new();
        ScanResult result = await new FileSystemScanner()
            .ScanAsync(tree.Root, options: TestOptions.Verbose(ScanBackendPreference.Portable), progress: progress);

        Assert.Equal(ScanBackend.Portable, result.Backend);
        Assert.Null(result.FallbackFrom);

        ScanProgress[] ticks = [.. progress.Ticks];

        // The opening tick predates selection; everything after names the backend.
        Assert.Contains(ticks, t => t.ActiveBackend == ScanBackend.Portable);
        Assert.All(ticks, t => Assert.Null(t.FallbackFrom));
        Assert.All(ticks, t => Assert.Null(t.FallbackReason));
    }
}
