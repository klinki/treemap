using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Attached (production) collector/tree path: every test here runs through
/// <see cref="FileSystemScanner"/> or an attached <see cref="ScanCollector"/>,
/// the incremental tree that shares immutable file nodes. The direct-collector
/// legacy tests only cover the unattached list and cannot validate this path.
/// </summary>
public class ScanAttachedTreeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string FakeRoot(string name)
        => Path.Combine(Path.GetTempPath(), "treemap-attached-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static ScanOptions PreviewOptions() => new()
    {
        Backend = ScanBackendPreference.Portable,
        ProgressInterval = TimeSpan.Zero,
        PublishIntermediateSnapshots = true,
        SnapshotInterval = TimeSpan.FromMilliseconds(20),
    };

    [Fact]
    public async Task FolderAndFileTimestampsSurviveTheScan()
    {
        DateTimeOffset folderTime = new(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        DateTimeOffset fileTime = new(2023, 1, 2, 3, 4, 5, TimeSpan.Zero);
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("docs", NodeKind.Folder, 0, 0, folderTime));
        backend.Entries.Add(new ScanEntry(Path.Combine("docs", "a.txt"), NodeKind.File, 10, 10, fileTime));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(FolderAndFileTimestampsSurviveTheScan)), options: TestOptions.Verbose());

        Assert.Equal(folderTime, TreeAssert.Find(result.Root, "docs")!.LastWriteTime);
        Assert.Equal(fileTime, TreeAssert.Find(result.Root, "docs", "a.txt")!.LastWriteTime);
        // No docs/docs duplication: the folder leaf is mutated once, never nested.
        TreemapNode docs = TreeAssert.Find(result.Root, "docs")!;
        Assert.Single(result.Root.Children);
        Assert.Single(docs.Children);
        Assert.Null(TreeAssert.Find(result.Root, "docs", "docs"));
        Assert.Equal(1, result.DirectoryCount);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(10, result.TotalLogicalSize);
    }

    [Fact]
    public async Task ChildBeforeParentKeepsStructureAndFolderTimestamp()
    {
        DateTimeOffset folderTime = new(2024, 2, 3, 4, 5, 6, TimeSpan.Zero);
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry(Path.Combine("sub", "inner.txt"), NodeKind.File, 100, 100, null));
        backend.Entries.Add(new ScanEntry("sub", NodeKind.Folder, 0, 0, folderTime));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(ChildBeforeParentKeepsStructureAndFolderTimestamp)), options: TestOptions.Verbose());

        TreemapNode sub = TreeAssert.Find(result.Root, "sub")!;
        Assert.Equal(NodeKind.Folder, sub.Kind);
        Assert.Equal(folderTime, sub.LastWriteTime);
        Assert.NotNull(TreeAssert.Find(result.Root, "sub", "inner.txt"));
        // Inferred parent promoted in place: no sub/sub, root holds one child.
        Assert.Single(result.Root.Children);
        Assert.Single(sub.Children);
        Assert.Null(TreeAssert.Find(result.Root, "sub", "sub"));
        Assert.Equal(100, result.TotalLogicalSize);
    }

    [Fact]
    public async Task DuplicateFileLastWinsButCountsBothEmissions()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("same.txt", NodeKind.File, 10, 10, null));
        backend.Entries.Add(new ScanEntry("same.txt", NodeKind.File, 99, 99, null));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(DuplicateFileLastWinsButCountsBothEmissions)), options: TestOptions.Verbose());

        // Legacy semantics: the flat list counted emissions while the builder collapsed
        // them last-wins. The attached path keeps that contract.
        Assert.Equal(2, result.FileCount);
        Assert.Equal(109, result.TotalLogicalSize);
        TreemapNode kept = Assert.Single(result.Root.Children);
        Assert.Equal(99, kept.LogicalSize);
    }

    [Fact]
    public async Task DuplicateFolderEmissionUpdatesTimestampLastWins()
    {
        DateTimeOffset first = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset second = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("docs", NodeKind.Folder, 0, 0, first));
        backend.Entries.Add(new ScanEntry("docs", NodeKind.Folder, 0, 0, second));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(DuplicateFolderEmissionUpdatesTimestampLastWins)), options: TestOptions.Verbose());

        Assert.Equal(2, result.DirectoryCount);
        Assert.Equal(second, TreeAssert.Find(result.Root, "docs")!.LastWriteTime);
        // Duplicate explicit emission updates in place: still one folder, no nesting.
        Assert.Single(result.Root.Children);
        Assert.Empty(TreeAssert.Find(result.Root, "docs")!.Children);
        Assert.Null(TreeAssert.Find(result.Root, "docs", "docs"));
    }

    [Fact]
    public async Task FolderEntryAfterFileReplacesIt_LikeLegacyBuilder()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("swap", NodeKind.File, 50, 50, null));
        backend.Entries.Add(new ScanEntry("swap", NodeKind.Folder, 0, 0, null));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(FolderEntryAfterFileReplacesIt_LikeLegacyBuilder)), options: TestOptions.Verbose());

        TreemapNode node = Assert.Single(result.Root.Children);
        Assert.Equal(NodeKind.Folder, node.Kind);
        Assert.Empty(node.Children);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.DirectoryCount);
    }

    [Fact]
    public async Task FileEntryAfterFolderReplacesIt_LikeLegacyBuilder()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("swap", NodeKind.Folder, 0, 0, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("swap", "inner.txt"), NodeKind.File, 10, 10, null));
        backend.Entries.Add(new ScanEntry("swap", NodeKind.File, 70, 70, null));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(FileEntryAfterFolderReplacesIt_LikeLegacyBuilder)), options: TestOptions.Verbose());

        TreemapNode node = Assert.Single(result.Root.Children);
        Assert.Equal(NodeKind.File, node.Kind);
        Assert.Equal(70, node.LogicalSize);
    }

    [Fact]
    public async Task PathBlockedByAFileCountsButStaysOutOfTheTree()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a", NodeKind.File, 10, 10, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("a", "b.txt"), NodeKind.File, 20, 20, null));

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(PathBlockedByAFileCountsButStaysOutOfTheTree)), options: TestOptions.Verbose());

        // Both emissions count (legacy parity) but the tree cannot nest under a file.
        Assert.Equal(2, result.FileCount);
        Assert.Equal(30, result.TotalLogicalSize);
        Assert.NotNull(TreeAssert.Find(result.Root, "a"));
        Assert.Null(TreeAssert.Find(result.Root, "a", "b.txt"));
    }

    [Fact]
    public void SiblingsShareOneFolderCharge()
    {
        const int Count = 200;
        long budget = Count * ScanMemoryBudget.EstimateFile(8) + ScanMemoryBudget.EstimateFolder(1) + 4096;
        ScanCollector collector = new(
            ScanOptions.Default with { MemoryBudgetBytes = budget }, progress: null);
        ScanIdMap ids = new();
        ids.BeginBuild(FakeRoot(nameof(SiblingsShareOneFolderCharge)));
        collector.AttachIds(ids);

        for (int i = 0; i < Count; i++)
        {
            Assert.True(collector.TryAdd(Path.Combine("d", $"f{i:D3}.bin"), NodeKind.File, 100, 100, null));
        }

        // The old per-sibling ancestor recharge would charge the folder 200 times and
        // stop early; charging actually-new parents once fits everything.
        Assert.Equal(Count, collector.FileCount);
        Assert.True(collector.IsComplete);
    }

    [Fact]
    public void FrozenSnapshotKeepsOldFolderTimestampAfterUpdate()
    {
        ScanCollector collector = new(ScanOptions.Default, progress: null);
        ScanIdMap ids = new();
        string root = FakeRoot(nameof(FrozenSnapshotKeepsOldFolderTimestampAfterUpdate));
        ids.BeginBuild(root);
        collector.AttachIds(ids);

        DateTimeOffset first = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset second = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.True(collector.TryAdd("docs", NodeKind.Folder, 0, 0, first));
        Assert.True(collector.TryAdd(Path.Combine("docs", "a.txt"), NodeKind.File, 10, 10, null));

        (ManagedScanTree.FolderSnapshot snap, _) = collector.CaptureTreeSnapshot();

        Assert.True(collector.TryAdd("docs", NodeKind.Folder, 0, 0, second));

        TreemapNode before = ScanCollector.BuildFromSnapshot(root, snap, CancellationToken.None);
        (ManagedScanTree.FolderSnapshot snap2, _) = collector.CaptureTreeSnapshot();
        TreemapNode after = ScanCollector.BuildFromSnapshot(root, snap2, CancellationToken.None);

        Assert.Equal(first, TreeAssert.Find(before, "docs")!.LastWriteTime);
        Assert.Equal(second, TreeAssert.Find(after, "docs")!.LastWriteTime);
        // The folder leaf was mutated once, never nested under itself.
        foreach (TreemapNode tree in new[] { before, after })
        {
            Assert.Single(tree.Children);
            Assert.Single(TreeAssert.Find(tree, "docs")!.Children);
            Assert.Null(TreeAssert.Find(tree, "docs", "docs"));
        }
    }

    [Fact]
    public void SuccessiveBuildsShareFileNodeInstances()
    {
        ScanCollector collector = new(ScanOptions.Default, progress: null);
        ScanIdMap ids = new();
        string root = FakeRoot(nameof(SuccessiveBuildsShareFileNodeInstances));
        ids.BeginBuild(root);
        collector.AttachIds(ids);

        Assert.True(collector.TryAdd(Path.Combine("docs", "a.txt"), NodeKind.File, 10, 10, null));
        (ManagedScanTree.FolderSnapshot first, _) = collector.CaptureTreeSnapshot();

        Assert.True(collector.TryAdd(Path.Combine("docs", "b.txt"), NodeKind.File, 20, 20, null));
        (ManagedScanTree.FolderSnapshot second, _) = collector.CaptureTreeSnapshot();

        TreemapNode preview = ScanCollector.BuildFromSnapshot(root, first, CancellationToken.None);
        TreemapNode final = ScanCollector.BuildFromSnapshot(root, second, CancellationToken.None);

        // File nodes are created once and shared; folder aggregates are rebuilt.
        Assert.Same(
            TreeAssert.Find(preview, "docs", "a.txt"),
            TreeAssert.Find(final, "docs", "a.txt"));
        Assert.NotSame(
            TreeAssert.Find(preview, "docs"),
            TreeAssert.Find(final, "docs"));
        Assert.Equal(
            TreeAssert.Find(preview, "docs")!.Id,
            TreeAssert.Find(final, "docs")!.Id);
    }

    [Fact]
    public void FinalFreezePreservesCapturedPreviewsAndCounts()
    {
        ScanCollector collector = new(ScanOptions.Default, progress: null);
        ScanIdMap ids = new();
        string root = FakeRoot(nameof(FinalFreezePreservesCapturedPreviewsAndCounts));
        ids.BeginBuild(root);
        collector.AttachIds(ids);

        Assert.True(collector.TryAdd("docs", NodeKind.Folder, 0, 0, null));
        Assert.True(collector.TryAdd(Path.Combine("docs", "a.txt"), NodeKind.File, 10, 10, null));
        Assert.True(collector.TryAdd(Path.Combine("docs", "b.txt"), NodeKind.File, 20, 20, null));

        (ManagedScanTree.FolderSnapshot snap, long represented) = collector.CaptureTreeSnapshot();
        TreemapNode before = ScanCollector.BuildFromSnapshot(root, snap, CancellationToken.None);
        TreemapNode final = collector.BuildFinalTreeDestructive(root, CancellationToken.None);

        // Counters and represented totals stay: the release only drops the mutable
        // working dictionaries, never the accounting the result reports.
        Assert.Equal(2, collector.FileCount);
        Assert.Equal(1, collector.DirectoryCount);
        Assert.Equal(3, collector.TotalEntryCount);
        Assert.Equal(represented, collector.RepresentedBytes);

        // The captured snapshot still builds the same tree over the same shared file
        // nodes after the working tree is gone.
        TreemapNode after = ScanCollector.BuildFromSnapshot(root, snap, CancellationToken.None);
        Assert.Same(
            TreeAssert.Find(before, "docs", "a.txt"),
            TreeAssert.Find(after, "docs", "a.txt"));
        Assert.Equal(30, after.LogicalSize);
        Assert.Equal(30, final.LogicalSize);
        Assert.Same(TreeAssert.Find(before, "docs", "a.txt"), TreeAssert.Find(final, "docs", "a.txt"));

        // A later capture sees the emptied working tree, proving the mutable
        // dictionaries were actually dropped rather than merely unreferenced.
        (ManagedScanTree.FolderSnapshot emptied, _) = collector.CaptureTreeSnapshot();
        Assert.Empty(emptied.Subfolders);
        Assert.Empty(emptied.Files);
    }

    [Fact]
    public async Task PreviewAndFinalShareFileIdentity_AndRescanKeepsIt()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("sub", "inner.txt"), NodeKind.File, 50, 50, null));

        string root = FakeRoot(nameof(PreviewAndFinalShareFileIdentity_AndRescanKeepsIt));
        FileSystemScanner scanner = new(backend, null);
        Task<ScanResult> scan = scanner.ScanAsync(root, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, 0);
            TreemapNode previewFile = TreeAssert.Find(preview.Root, "early.txt")!;
            int previewFolderId = TreeAssert.Find(preview.Root, "sub")!.Id;

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Same(previewFile, TreeAssert.Find(result.Root, "early.txt"));
            Assert.Equal(previewFile.Id, TreeAssert.Find(result.Root, "early.txt")!.Id);
            Assert.Equal(previewFolderId, TreeAssert.Find(result.Root, "sub")!.Id);

            ScanResult second = await scanner.ScanAsync(root, options: TestOptions.Verbose());
            Assert.Equal(previewFile.Id, TreeAssert.Find(second.Root, "early.txt")!.Id);
            Assert.Equal(previewFolderId, TreeAssert.Find(second.Root, "sub")!.Id);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task CancelledScanAbortsIdentity_SameRootKeepsCommittedIds()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        string root = FakeRoot(nameof(CancelledScanAbortsIdentity_SameRootKeepsCommittedIds));
        FileSystemScanner scanner = new(backend, null);

        backend.Release.Set();
        ScanResult first = await scanner.ScanAsync(root, options: TestOptions.Verbose());
        int committedId = TreeAssert.Find(first.Root, "early.txt")!.Id;

        backend.Entered.Reset();
        backend.Release.Reset();
        TreemapSnapshot prior = publisher.Current;
        using CancellationTokenSource cts = new();
        Task<ScanResult> doomed = scanner.ScanAsync(root, publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, prior.Version);
            cts.Cancel();
            backend.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => doomed);
        }
        finally
        {
            backend.Release.Set();
        }

        ScanResult rescanned = await scanner.ScanAsync(root, options: TestOptions.Verbose());
        Assert.Equal(committedId, TreeAssert.Find(rescanned.Root, "early.txt")!.Id);
    }

    [Fact]
    public async Task FallbackWithoutPreviewsForgetsAbandonedIds()
    {
        string root = FakeRoot(nameof(FallbackWithoutPreviewsForgetsAbandonedIds));
        ScriptedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "MFT unreadable",
        };
        fast.Entries.Add(new ScanEntry("ghost.txt", NodeKind.File, 5000, 5000, null));
        ScriptedBackend portable = new(ScanBackend.Portable);
        portable.Entries.Add(new ScanEntry("real.txt", NodeKind.File, 77, 77, null));

        FileSystemScanner scanner = new(portable, fast);
        ScanResult first = await scanner.ScanAsync(
            root, options: TestOptions.Verbose(ScanBackendPreference.Auto));

        Assert.Equal(ScanBackend.Portable, first.Backend);
        Assert.Null(TreeAssert.Find(first.Root, "ghost.txt"));
        int realId = TreeAssert.Find(first.Root, "real.txt")!.Id;

        // The ghost was only in the abandoned walk. Reintroduce it for real: it must not
        // reuse an id from the discarded preview-less walk... but without previews the
        // abandoned walk never published, so capture the ordering indirectly: the accepted
        // path keeps its id while the ghost gets a fresh one greater than any committed.
        fast.Entries.Clear();
        portable.Entries.Add(new ScanEntry("ghost.txt", NodeKind.File, 5000, 5000, null));
        ScanResult second = await scanner.ScanAsync(
            root, options: TestOptions.Verbose(ScanBackendPreference.Auto));

        Assert.Equal(realId, TreeAssert.Find(second.Root, "real.txt")!.Id);
        Assert.NotNull(TreeAssert.Find(second.Root, "ghost.txt"));
        int ghostId = TreeAssert.Find(second.Root, "ghost.txt")!.Id;
        // Ids are monotonic and never reused: the accepted path keeps its committed id
        // while the ghost - seen only in the discarded walk - mints a fresh id after it.
        // A reuse of the abandoned reservation would order below the committed one.
        Assert.NotEqual(realId, ghostId);
        Assert.True(ghostId > realId, $"Expected fresh ghost id after {realId}, got {ghostId}.");
    }

    [Fact]
    public async Task FolderChildReplacedByFileGetsFreshChildIdWhenRestored()
    {
        // Contradictory scripted order only: a folder with a child, then a file at the
        // folder's path, drops the subtree. Real backends never emit children under a
        // file (the portable backend emits a parent before its children and never walks
        // under a file), but the pruning contract still holds: a path absent from the
        // returned tree gets a fresh id when it returns.
        string root = FakeRoot(nameof(FolderChildReplacedByFileGetsFreshChildIdWhenRestored));
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("swap", NodeKind.Folder, 0, 0, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("swap", "inner.txt"), NodeKind.File, 10, 10, null));
        FileSystemScanner scanner = new(backend, null);
        ScanResult original = await scanner.ScanAsync(root, options: TestOptions.Verbose());
        int originalChildId = TreeAssert.Find(original.Root, "swap", "inner.txt")!.Id;

        backend.Entries.Add(new ScanEntry("swap", NodeKind.File, 70, 70, null));
        ScanResult replaced = await scanner.ScanAsync(root, options: TestOptions.Verbose());

        TreemapNode fileLeaf = Assert.Single(replaced.Root.Children);
        Assert.Equal(NodeKind.File, fileLeaf.Kind);
        int fileId = fileLeaf.Id;

        backend.Entries.Clear();
        backend.Entries.Add(new ScanEntry("swap", NodeKind.Folder, 0, 0, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("swap", "inner.txt"), NodeKind.File, 10, 10, null));
        ScanResult restored = await scanner.ScanAsync(root, options: TestOptions.Verbose());

        TreemapNode folder = TreeAssert.Find(restored.Root, "swap")!;
        Assert.Equal(NodeKind.Folder, folder.Kind);
        TreemapNode? child = TreeAssert.Find(restored.Root, "swap", "inner.txt");
        Assert.NotNull(child);
        // The leaf key is reused across the kind flip (same path, still present as the
        // scan's last word on it); the dropped child was pruned from the pending map at
        // commit, so its return mints a fresh id rather than reusing the discarded one.
        Assert.Equal(fileId, folder.Id);
        Assert.NotEqual(originalChildId, child.Id);
    }

    [Fact]
    public async Task RemovedFileGetsAFreshIdWhenItReturns()
    {
        using TempTree tree = new(nameof(RemovedFileGetsAFreshIdWhenItReturns));
        tree.MakeFile(Path.Combine("sub", "transient.txt"), 5);
        FileSystemScanner scanner = new();

        ScanResult first = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        int originalId = TreeAssert.Find(first.Root, "sub", "transient.txt")!.Id;

        File.Delete(Path.Combine(tree.Root, "sub", "transient.txt"));
        ScanResult second = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        Assert.Null(TreeAssert.Find(second.Root, "sub", "transient.txt"));

        tree.MakeFile(Path.Combine("sub", "transient.txt"), 5);
        ScanResult third = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        Assert.NotEqual(originalId, TreeAssert.Find(third.Root, "sub", "transient.txt")!.Id);
    }

    [Fact]
    public void ChildKeyCaseFollowsPlatformRules()
    {
        ScanIdMap ids = new();
        string root = FakeRoot(nameof(ChildKeyCaseFollowsPlatformRules));
        ids.BeginBuild(root);
        int parent = ids.ReserveChild(ScanIdMap.RootId, "sub");
        int first = ids.ReserveChild(parent, "Mixed.txt");
        int second = ids.ReserveChild(parent, "mixed.txt");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(first, second);
        }
        else
        {
            Assert.NotEqual(first, second);
        }
    }

    [Fact]
    public void SameBasenameUnderDifferentParentsGetsDifferentIds()
    {
        ScanIdMap ids = new();
        string root = FakeRoot(nameof(SameBasenameUnderDifferentParentsGetsDifferentIds));
        ids.BeginBuild(root);
        int parentA = ids.ReserveChild(ScanIdMap.RootId, "a");
        int parentB = ids.ReserveChild(ScanIdMap.RootId, "b");

        Assert.NotEqual(ids.ReserveChild(parentA, "same.txt"), ids.ReserveChild(parentB, "same.txt"));
    }

    private static async Task<TreemapSnapshot> WaitForPreviewAsync(SnapshotPublisher publisher, long versionBefore)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            TreemapSnapshot current = publisher.Current;
            if (current.Version > versionBefore && current.Root.Children.Count > 0)
            {
                return current;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No preview was published before the timeout.");
    }

    /// <summary>
    /// Adds entries, then waits for release, so previews have a window to fire mid-scan.
    /// </summary>
    private sealed class GatedBackend(ScanBackend id) : IScanBackend
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

            try
            {
                Release.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            return true;
        }
    }
}
