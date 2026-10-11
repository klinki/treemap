using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Identity, publication and cancellation - the parts a UI host depends on.
/// </summary>
public class ScanContractTests
{
    [Fact]
    public async Task RootTakesIdOneAndTheHostPlaceholderMatches()
    {
        using TempTree tree = new(nameof(RootTakesIdOneAndTheHostPlaceholderMatches));
        tree.MakeFile("a.txt", 1);

        SnapshotPublisher publisher = TestPublisher.Empty();
        Assert.Equal(ScanIdMap.RootId, publisher.Current.Root.Id);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, publisher, TestOptions.Verbose());

        Assert.Equal(ScanIdMap.RootId, result.Root.Id);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(ScanIdMap.RootId, result.Snapshot!.Root.Id);
        Assert.True(result.Snapshot.TryGetNode(result.Root.Id, out _));
    }

    [Fact]
    public async Task EveryNodeInASnapshotHasADistinctId()
    {
        using TempTree tree = new(nameof(EveryNodeInASnapshotHasADistinctId));
        tree.MakeDirectory("a", "b");
        tree.MakeFile(Path.Combine("a", "b", "deep.txt"), 3);
        tree.MakeFile("top.txt", 4);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        int[] ids = [.. TreeAssert.Flatten(result.Root).Select(n => n.Id)];
        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.DoesNotContain(0, ids);
    }

    /// <summary>
    /// The reason ids exist at all: adding a sibling must not renumber the folders a user
    /// is already looking at, or the controller's focus jumps to a different directory.
    /// </summary>
    [Fact]
    public async Task IdsStayWithTheirPathsWhenASiblingAppears()
    {
        using TempTree tree = new(nameof(IdsStayWithTheirPathsWhenASiblingAppears));
        tree.MakeDirectory("aaa");
        tree.MakeDirectory("mmm");

        FileSystemScanner scanner = new();

        ScanResult first = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        int aaaId = TreeAssert.Find(first.Root, "aaa")!.Id;
        int mmmId = TreeAssert.Find(first.Root, "mmm")!.Id;

        // Sorting puts "aaa-new" before "mmm", so a positional numbering would shift mmm.
        tree.MakeDirectory("aaa-new");

        ScanResult second = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.Equal(aaaId, TreeAssert.Find(second.Root, "aaa")!.Id);
        Assert.Equal(mmmId, TreeAssert.Find(second.Root, "mmm")!.Id);
        Assert.NotEqual(aaaId, TreeAssert.Find(second.Root, "aaa-new")!.Id);
    }

    /// <summary>
    /// The guarantee covers what successive trees actually showed. A path that vanished is
    /// gone from the map, so a returning path is a new node - which is what keeps the map
    /// bounded.
    /// </summary>
    [Fact]
    public async Task ARemovedPathGetsANewIdWhenItComesBack()
    {
        using TempTree tree = new(nameof(ARemovedPathGetsANewIdWhenItComesBack));
        string transient = tree.MakeDirectory("temp");

        FileSystemScanner scanner = new();

        ScanResult first = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        int originalId = TreeAssert.Find(first.Root, "temp")!.Id;

        Directory.Delete(transient);
        ScanResult second = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());
        Assert.Null(TreeAssert.Find(second.Root, "temp"));

        Directory.CreateDirectory(transient);
        ScanResult third = await scanner.ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.NotEqual(originalId, TreeAssert.Find(third.Root, "temp")!.Id);
    }

    /// <summary>
    /// The whole point of publishing from a worker: a UI thread that calls this must not
    /// have the tree built or handed off on it.
    /// </summary>
    [Fact]
    public async Task PublishRunsOnAWorkerThreadNotTheCallers()
    {
        using TempTree tree = new(nameof(PublishRunsOnAWorkerThreadNotTheCallers));
        tree.MakeFile("a.txt", 8);

        SnapshotPublisher publisher = TestPublisher.Empty();
        int publishThread = -1;
        publisher.Published += () => publishThread = Environment.CurrentManagedThreadId;

        Task<ScanResult>? scan = null;
        int callerThreadId = 0;

        Thread caller = new(() => scan = new FileSystemScanner().ScanAsync(tree.Root, publisher, TestOptions.Verbose()))
        {
            IsBackground = true,
        };

        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(30)), "the calling thread never returned");
        callerThreadId = caller.ManagedThreadId;

        ScanResult result = await scan!;

        Assert.NotNull(result.Snapshot);
        Assert.NotEqual(callerThreadId, publishThread);
    }

    [Fact]
    public async Task AlreadyCancelledScanThrowsAndPublishesNothing()
    {
        using TempTree tree = new(nameof(AlreadyCancelledScanThrowsAndPublishesNothing));
        tree.MakeFile("a.txt", 1);

        SnapshotPublisher publisher = TestPublisher.Empty();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FileSystemScanner().ScanAsync(tree.Root, publisher, TestOptions.Verbose(), cancellationToken: cts.Token));

        Assert.Equal(0L, publisher.Current.Version);
        Assert.Empty(publisher.Current.Root.Children);
    }

    /// <summary>
    /// Cancelling from inside a progress callback is the realistic case: a Cancel button
    /// runs while entries are still arriving. It must stop and publish nothing.
    /// </summary>
    [Fact]
    public async Task CancellingMidScanPublishesNothing()
    {
        using TempTree tree = new(nameof(CancellingMidScanPublishesNothing));

        for (int i = 0; i < 600; i++)
        {
            tree.MakeFile($"f{i:D4}.bin", i % 5);
        }

        SnapshotPublisher publisher = TestPublisher.Empty();
        using CancellationTokenSource cts = new();
        CancellingProgress progress = new(cts, 20);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FileSystemScanner().ScanAsync(
                tree.Root,
                publisher,
                TestOptions.Verbose(),
                progress,
                cts.Token));

        Assert.True(progress.Reports > 0, "the scan never reported progress to cancel from");
        Assert.Equal(0L, publisher.Current.Version);
    }

    [Fact]
    public async Task ProgressReachesTheFinishedPhase()
    {
        using TempTree tree = new(nameof(ProgressReachesTheFinishedPhase));
        tree.MakeFile("a.txt", 1);

        RecordingProgress progress = new();

        await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose(), progress: progress);

        ScanProgress[] ticks = [.. progress.Ticks];

        Assert.Contains(ticks, t => t.Phase == ScanPhase.Starting);
        Assert.Contains(ticks, t => t.Phase == ScanPhase.Enumerating);
        Assert.Contains(ticks, t => t.Phase == ScanPhase.Finished);
        Assert.All(ticks, t => Assert.True(t.EntriesScanned >= 0));
    }

    /// <summary>
    /// A progress callback that throws is the host's bug, not an unreadable directory.
    /// Recording it as a filesystem problem would both hide it and quietly truncate the
    /// scan it interrupted.
    /// </summary>
    [Fact]
    public async Task AFailingProgressCallbackSurfacesRatherThanBecomingADiagnostic()
    {
        using TempTree tree = new(nameof(AFailingProgressCallbackSurfacesRatherThanBecomingADiagnostic));
        tree.MakeDirectory("sub");
        tree.MakeFile(Path.Combine("sub", "a.txt"), 1);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => new FileSystemScanner().ScanAsync(
                tree.Root,
                options: TestOptions.Verbose(),
                progress: new FailingProgress()));

        Assert.Equal("the host callback failed", error.Message);
    }

    /// <summary>Free space describes a whole disk, so it never pads a folder scan.</summary>
    [Fact]
    public async Task FreeSpaceIsNeverAddedToAFolderScan()
    {
        using TempTree tree = new(nameof(FreeSpaceIsNeverAddedToAFolderScan));
        tree.MakeFile("a.txt", 1);

        ScanResult result = await new FileSystemScanner().ScanAsync(tree.Root, options: TestOptions.Verbose());

        Assert.Equal(NodeKind.Folder, result.Root.Kind);
        Assert.Equal(0, result.FreeSpaceBytes);
        Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
    }
}