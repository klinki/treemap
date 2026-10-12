using System.Diagnostics;
using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Live previews: early publish, stable ids, and restoring the prior map on
/// cancellation, failure and fallback. Backends are gated so a preview must
/// appear before the scan is allowed to finish.
/// </summary>
public class ScanPreviewTests
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ScanOptions PreviewOptions(ScanBackendPreference backend = ScanBackendPreference.Portable) => new()
    {
        Backend = backend,
        ProgressInterval = TimeSpan.Zero,
        PublishIntermediateSnapshots = true,
        SnapshotInterval = PreviewInterval,
    };

    private static string FakeRoot(string name)
        => Path.Combine(Path.GetTempPath(), "treemap-preview-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static async Task<TreemapSnapshot> WaitForPreviewAsync(SnapshotPublisher publisher, long versionBefore)
    {
        Stopwatch clock = Stopwatch.StartNew();
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

    [Fact]
    public async Task PreviewPublishedBeforeCompletion_AndFinalKeepsIds()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));
        backend.Entries.Add(new ScanEntry("late.txt", NodeKind.File, 200, 200, null));

        FileSystemScanner scanner = new(backend, null);
        string root = FakeRoot(nameof(PreviewPublishedBeforeCompletion_AndFinalKeepsIds));
        Task<ScanResult> scan = scanner.ScanAsync(root, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, 0);
            Assert.NotNull(TreeAssert.Find(preview.Root, "early.txt"));
            int previewId = TreeAssert.Find(preview.Root, "early.txt")!.Id;

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(2, result.FileCount);
            Assert.Equal(previewId, TreeAssert.Find(result.Root, "early.txt")!.Id);

            // A rescan keeps the committed ids too. The gated backend replays the
            // same entries and its release is already set, so it runs straight through.
            ScanResult second = await scanner.ScanAsync(root, options: TestOptions.Verbose());
            Assert.Equal(previewId, TreeAssert.Find(second.Root, "early.txt")!.Id);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task DefaultOptionsPublishNothingMidScan()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(DefaultOptionsPublishNothingMidScan)), publisher, TestOptions.Verbose());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;
            Assert.Equal(1, result.FileCount);
            Assert.Equal(1, publisher.Current.Version);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task CancellationAfterPreviewRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        using CancellationTokenSource cts = new();
        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(CancellationAfterPreviewRestoresPriorMap)), publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, prior.Version);

            cts.Cancel();
            backend.Release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            Assert.Same(prior.Root, publisher.Current.Root);
            Assert.Null(TreeAssert.Find(publisher.Current.Root, "early.txt"));
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task FailureAfterPreviewRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        GatedBackend backend = new(ScanBackend.Portable)
        {
            ThrowAfterRelease = new IOException("the backend broke mid-scan"),
        };
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(FailureAfterPreviewRestoresPriorMap)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, prior.Version);

            backend.Release.Set();
            IOException error = await Assert.ThrowsAsync<IOException>(() => scan);
            Assert.Equal("the backend broke mid-scan", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task FallbackDiscardsAbandonedPreviewData()
    {
        using TempTree tree = new(nameof(FallbackDiscardsAbandonedPreviewData));
        tree.MakeFile("real.txt", 77);

        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        GatedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "MFT unreadable",
        };
        fast.Entries.Add(new ScanEntry("partial-from-mft.txt", NodeKind.File, 5000, 5000, null));

        Task<ScanResult> scan = new FileSystemScanner(null, fast)
            .ScanAsync(tree.Root, publisher, PreviewOptions(ScanBackendPreference.Auto));

        try
        {
            Assert.True(fast.Entered.Wait(Timeout), "The fast backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(preview.Root, "partial-from-mft.txt"));

            fast.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(ScanBackend.Portable, result.Backend);
            Assert.Equal(ScanBackend.NtfsMft, result.FallbackFrom);
            Assert.Null(TreeAssert.Find(result.Root, "partial-from-mft.txt"));
            Assert.NotNull(TreeAssert.Find(result.Root, "real.txt"));
            Assert.Null(TreeAssert.Find(publisher.Current.Root, "partial-from-mft.txt"));
        }
        finally
        {
            fast.Release.Set();
        }
    }

    [Fact]
    public async Task PreviewSubscriberFailureFailsScanAndRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        int calls = 0;
        publisher.Published += () =>
        {
            // Throws once, on the first preview: the snapshot is already visible,
            // so the scan must fail and restore the prior map rather than carry on.
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new IOException("subscriber blew up once");
            }
        };

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(PreviewSubscriberFailureFailsScanAndRestoresPriorMap)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, 0);

            backend.Release.Set();
            IOException error = await Assert.ThrowsAsync<IOException>(() => scan);
            Assert.Equal("subscriber blew up once", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);

            // One preview plus its restore; the unchanged entries never republished.
            Assert.Equal(2, calls);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task SubscriberOperationCanceledExceptionWithoutScanCancellationFailsScan()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        int calls = 0;
        publisher.Published += () =>
        {
            // The scan token is never canceled: this OCE is a subscriber fault and
            // must fail the scan, not be swallowed as a cancellation.
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new OperationCanceledException("subscriber canceled, not the scan");
            }
        };

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(SubscriberOperationCanceledExceptionWithoutScanCancellationFailsScan)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, 0);

            backend.Release.Set();
            OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() => scan);
            Assert.Equal("subscriber canceled, not the scan", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);

            // One preview plus its restore; the unchanged entries never republished.
            Assert.Equal(2, calls);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task BlockedPreviewSubscriberCannotPublishAfterAbort()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        using ManualResetEventSlim subscriberEntered = new(false);
        using ManualResetEventSlim releaseSubscriber = new(false);
        int subscriberCalls = 0;
        publisher.Published += () =>
        {
            if (Interlocked.Increment(ref subscriberCalls) == 1)
            {
                subscriberEntered.Set();

                // Block the preview build mid-publish while the scan is cancelled:
                // Stop must drain this before it aborts identity or restores.
                Assert.True(releaseSubscriber.Wait(Timeout), "The test never released the subscriber.");
            }
        };

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        using CancellationTokenSource cts = new();
        FileSystemScanner scanner = new(backend, null);
        Task<ScanResult> scan = scanner.ScanAsync(
            FakeRoot(nameof(BlockedPreviewSubscriberCannotPublishAfterAbort)), publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            Assert.True(subscriberEntered.Wait(Timeout), "No preview publish started.");

            cts.Cancel();
            backend.Release.Set();

            // The scan thread is now stuck draining the blocked publish: the prior
            // map is not restored yet, proving Stop really waited it out.
            await Task.Delay(250);
            Assert.NotSame(prior.Root, publisher.Current.Root);
            Assert.NotNull(TreeAssert.Find(publisher.Current.Root, "early.txt"));

            releaseSubscriber.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            Assert.Same(prior.Root, publisher.Current.Root);

            // No late publish may overwrite the restore after the abort.
            await Task.Delay(300);
            Assert.Same(prior.Root, publisher.Current.Root);

            // Identity survived the abort: the next scan still builds and commits.
            backend.Entries.Add(new ScanEntry("later.txt", NodeKind.File, 50, 50, null));
            ScanResult second = await scanner.ScanAsync(
                FakeRoot("blocked-subscriber-rescan"), options: TestOptions.Verbose());
            Assert.NotNull(TreeAssert.Find(second.Root, "later.txt"));
        }
        finally
        {
            releaseSubscriber.Set();
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task FinalPublishFailureAfterPreviewRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        publisher.Published += () =>
        {
            // Throws only for the final tree, the first to contain second.txt.
            if (TreeAssert.Find(publisher.Current.Root, "second.txt") is not null)
            {
                throw new IOException("final subscriber blew up");
            }
        };

        StagedBackend backend = new(ScanBackend.Portable);
        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(FinalPublishFailureAfterPreviewRestoresPriorMap)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.Null(TreeAssert.Find(preview.Root, "second.txt"));

            backend.ReleaseSecond.Set();
            IOException error = await Assert.ThrowsAsync<IOException>(() => scan);
            Assert.Equal("final subscriber blew up", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);
        }
        finally
        {
            backend.ReleaseSecond.Set();
        }
    }

    [Fact]
    public async Task LegacyFinalPublishFailureLeavesPublisherAlone()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        publisher.Published += () => throw new IOException("legacy subscriber blew up");

        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        IOException error = await Assert.ThrowsAsync<IOException>(() => new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(LegacyFinalPublishFailureLeavesPublisherAlone)), publisher, TestOptions.Verbose()));

        Assert.Equal("legacy subscriber blew up", error.Message);

        // Legacy mode performs no restore: the published final tree stays visible.
        Assert.NotSame(prior.Root, publisher.Current.Root);
        Assert.NotNull(TreeAssert.Find(publisher.Current.Root, "a.txt"));
    }

    [Fact]
    public async Task OptInFinalPublishFailureWithoutPreviewRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        Assert.Null(prior.RootPath);
        publisher.Published += () => throw new IOException("final subscriber blew up");

        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        // An interval the scan finishes well inside, so no preview can fire: only
        // the final publish throws, and the opt-in restore must still run.
        ScanOptions options = PreviewOptions() with { SnapshotInterval = TimeSpan.FromHours(1) };

        IOException error = await Assert.ThrowsAsync<IOException>(() => new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(OptInFinalPublishFailureWithoutPreviewRestoresPriorMap)), publisher, options));

        Assert.Equal("final subscriber blew up", error.Message);
        Assert.Same(prior.Root, publisher.Current.Root);
        Assert.Null(publisher.Current.RootPath);
    }

    [Fact]
    public async Task PreviewAndFinalCarryTheScannedSource()
    {
        SnapshotPublisher publisher = PublisherWithSource("prior-source");
        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(PreviewAndFinalCarryTheScannedSource)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, 0);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(result.RootPath, preview.RootPath);
            Assert.Equal(result.RootPath, publisher.Current.RootPath);
            Assert.Equal(result.RootPath, result.Snapshot?.RootPath);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task CancellationAfterPreviewRestoresPriorSource()
    {
        SnapshotPublisher publisher = PublisherWithSource("prior-source");
        TreemapSnapshot prior = publisher.Current;

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("early.txt", NodeKind.File, 100, 100, null));

        using CancellationTokenSource cts = new();
        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(CancellationAfterPreviewRestoresPriorSource)), publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.NotEqual("prior-source", preview.RootPath);

            cts.Cancel();
            backend.Release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            Assert.Same(prior.Root, publisher.Current.Root);
            Assert.Equal("prior-source", publisher.Current.RootPath);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task FallbackRestoreKeepsPriorSource_AndFinalTakesScannedSource()
    {
        SnapshotPublisher publisher = PublisherWithSource("prior-source");
        TreemapSnapshot prior = publisher.Current;

        GatedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "MFT unreadable",
        };
        fast.Entries.Add(new ScanEntry("partial-from-mft.txt", NodeKind.File, 5000, 5000, null));

        StagedPortableBackend portable = new();
        Task<ScanResult> scan = new FileSystemScanner(portable, fast)
            .ScanAsync(FakeRoot(nameof(FallbackRestoreKeepsPriorSource_AndFinalTakesScannedSource)), publisher, PreviewOptions(ScanBackendPreference.Auto));

        try
        {
            Assert.True(fast.Entered.Wait(Timeout), "The fast backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(preview.Root, "partial-from-mft.txt"));

            fast.Release.Set();
            Assert.True(portable.Entered.Wait(Timeout), "The portable backend never started.");

            // The abandonment restored the prior map, and nothing has republished
            // since: the portable retry has not collected anything yet.
            Assert.Same(prior.Root, publisher.Current.Root);
            Assert.Equal("prior-source", publisher.Current.RootPath);

            portable.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(ScanBackend.Portable, result.Backend);
            Assert.Null(TreeAssert.Find(result.Root, "partial-from-mft.txt"));
            Assert.NotNull(TreeAssert.Find(result.Root, "real.txt"));
            Assert.Equal(result.RootPath, publisher.Current.RootPath);
            Assert.Same(result.Root, publisher.Current.Root);
        }
        finally
        {
            fast.Release.Set();
            portable.Release.Set();
        }
    }

    [Fact]
    public async Task AbandonedPreviewIdsAreForgottenOnRetry()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        GatedBackend fast = new(ScanBackend.NtfsMft)
        {
            Succeeds = false,
            FailureReason = "MFT unreadable",
        };
        fast.Entries.Add(new ScanEntry("ghost.txt", NodeKind.File, 5000, 5000, null));

        ScriptedBackend portable = new(ScanBackend.Portable);
        portable.Entries.Add(new ScanEntry("real.txt", NodeKind.File, 77, 77, null));

        // One root for both scans, so absolute paths - and therefore ids - are
        // directly comparable across the abandonment and the rescan.
        string root = FakeRoot(nameof(AbandonedPreviewIdsAreForgottenOnRetry));
        FileSystemScanner scanner = new(portable, fast);
        Task<ScanResult> first = scanner.ScanAsync(root, publisher, PreviewOptions(ScanBackendPreference.Auto));

        int abandonedGhostId;
        int realId;
        try
        {
            Assert.True(fast.Entered.Wait(Timeout), "The fast backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(preview.Root, "ghost.txt"));
            abandonedGhostId = TreeAssert.Find(preview.Root, "ghost.txt")!.Id;

            fast.Release.Set();
            ScanResult result = await first;
            Assert.Equal(ScanBackend.Portable, result.Backend);
            Assert.Null(TreeAssert.Find(result.Root, "ghost.txt"));
            realId = TreeAssert.Find(result.Root, "real.txt")!.Id;
        }
        finally
        {
            fast.Release.Set();
        }

        // The ghost path was only ever in the discarded preview. When the next
        // scan shows it for real, it must get a fresh id; the accepted path keeps
        // its committed one. The fast decline replays empty and immediate.
        fast.Entries.Clear();
        portable.Entries.Add(new ScanEntry("ghost.txt", NodeKind.File, 5000, 5000, null));

        ScanResult second = await scanner.ScanAsync(root, options: TestOptions.Verbose(ScanBackendPreference.Auto));

        Assert.Equal(realId, TreeAssert.Find(second.Root, "real.txt")!.Id);
        Assert.NotEqual(abandonedGhostId, TreeAssert.Find(second.Root, "ghost.txt")!.Id);
    }

    [Fact]
    public async Task EmptyScanPublishesNoPreviews()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        GatedBackend backend = new(ScanBackend.Portable);

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(EmptyScanPublishesNoPreviews)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;
            Assert.Equal(0, result.FileCount);
            Assert.Equal(1, publisher.Current.Version);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task UnchangedEntriesAreNotRepublished()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        int publishes = 0;
        publisher.Published += () => Interlocked.Increment(ref publishes);

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(UnchangedEntriesAreNotRepublished)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForPreviewAsync(publisher, 0);
            await Task.Delay(400);

            backend.Release.Set();
            ScanResult result = await scan;
            Assert.Equal(1, result.FileCount);
            Assert.True(publishes <= 3, $"Expected one preview plus the final publish, saw {publishes}.");
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task ZeroIntervalCompletesOnATinyBackend()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 5, 5, null));
        backend.Entries.Add(new ScanEntry("b.txt", NodeKind.File, 7, 7, null));

        SnapshotPublisher publisher = TestPublisher.Empty();
        ScanOptions options = PreviewOptions() with { SnapshotInterval = TimeSpan.Zero };

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(ZeroIntervalCompletesOnATinyBackend)), publisher, options);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(12, result.TotalLogicalSize);
    }

    [Fact]
    public void PreviewDefaultsStayOff()
    {
        Assert.False(ScanOptions.Default.PublishIntermediateSnapshots);
        Assert.Equal(TimeSpan.FromMilliseconds(500), ScanOptions.Default.SnapshotInterval);
    }

    [Fact]
    public void NegativeSnapshotIntervalIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScanOptions { SnapshotInterval = TimeSpan.FromMilliseconds(-1) }.Validate());
    }

    [Fact]
    public void SnapshotIntervalAboveTimerBoundIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ScanOptions { SnapshotInterval = ScanOptions.MaxSnapshotInterval + TimeSpan.FromMilliseconds(1) }.Validate());

        // The bound itself is accepted.
        new ScanOptions { SnapshotInterval = ScanOptions.MaxSnapshotInterval }.Validate();
    }

    [Fact]
    public async Task SubMillisecondIntervalCompletesOnATinyBackend()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 5, 5, null));

        SnapshotPublisher publisher = TestPublisher.Empty();
        ScanOptions options = PreviewOptions() with { SnapshotInterval = TimeSpan.FromTicks(1) };

        ScanResult result = await new FileSystemScanner(backend, null)
            .ScanAsync(FakeRoot(nameof(SubMillisecondIntervalCompletesOnATinyBackend)), publisher, options);

        Assert.Equal(1, result.FileCount);
    }

    [Fact]
    public async Task PreviewAllocationOomPausesPreviewsAndFinalStillCompletes()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));
        backend.Entries.Add(new ScanEntry("b.txt", NodeKind.File, 20, 20, null));
        backend.Entries.Add(new ScanEntry(Path.Combine("sub", "c.txt"), NodeKind.File, 30, 30, null));

        FileSystemScanner scanner = new(backend, null);
        int faults = 0;
        scanner.PreviewAllocationFault = () =>
            Interlocked.Increment(ref faults) == 1
                ? new OutOfMemoryException("simulated preview allocation failure")
                : null;

        Task<ScanResult> scan = scanner.ScanAsync(
            FakeRoot(nameof(PreviewAllocationOomPausesPreviewsAndFinalStillCompletes)), publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            // Give the timer a window to attempt the preview and pause on OOM.
            await Task.Delay(500);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(1, faults);
            Assert.Equal(3, result.FileCount);
            Assert.NotNull(TreeAssert.Find(result.Root, "a.txt"));
            Assert.NotNull(TreeAssert.Find(result.Root, "b.txt"));
            Assert.NotNull(TreeAssert.Find(result.Root, "sub", "c.txt"));
            // The final full tree still published; no empty restore from the lost preview.
            Assert.Same(result.Root, publisher.Current.Root);
            Assert.NotSame(prior.Root, publisher.Current.Root);
            // A paused preview is a lost optional view, not a truncated inventory.
            Assert.True(result.IsComplete);
            Assert.Contains(result.Diagnostics, d =>
                d.Severity == ScanDiagnosticSeverity.Warning &&
                d.Message.Contains("previews stopped", StringComparison.OrdinalIgnoreCase));

            // Identity committed by the final tree survives: a rescan keeps it.
            int finalId = TreeAssert.Find(result.Root, "a.txt")!.Id;
            ScanResult second = await scanner.ScanAsync(result.RootPath, options: TestOptions.Verbose());
            Assert.Equal(finalId, TreeAssert.Find(second.Root, "a.txt")!.Id);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task PreviewSubscriberOomAfterPublishFailsScanAndRestoresPriorMap()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;
        int calls = 0;
        publisher.Published += () =>
        {
            // The snapshot is already visible when handlers run, so this OOM is a
            // subscriber failure and must fail the scan like any other handler fault.
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new OutOfMemoryException("subscriber ran out of memory");
            }
        };

        GatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.txt", NodeKind.File, 10, 10, null));

        FileSystemScanner scanner = new(backend, null);
        string scanRoot = FakeRoot(nameof(PreviewSubscriberOomAfterPublishFailsScanAndRestoresPriorMap));
        Task<ScanResult> scan = scanner.ScanAsync(
            scanRoot, publisher, PreviewOptions());

        int abandonedId;
        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot preview = await WaitForPreviewAsync(publisher, 0);
            abandonedId = TreeAssert.Find(preview.Root, "a.txt")!.Id;

            backend.Release.Set();
            OutOfMemoryException error = await Assert.ThrowsAsync<OutOfMemoryException>(() => scan);
            Assert.Equal("subscriber ran out of memory", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);

            // One preview plus its restore; the unchanged entries never republished.
            Assert.Equal(2, calls);
        }
        finally
        {
            backend.Release.Set();
        }

        // The failed build aborted its ids: the same root rescanned mints a fresh id.
        // Release stays set, so the gated retry runs straight through.
        ScanResult second = await scanner.ScanAsync(scanRoot, options: TestOptions.Verbose());
        Assert.NotEqual(abandonedId, TreeAssert.Find(second.Root, "a.txt")!.Id);
    }

    [Fact]
    public async Task CancelAfterPausedPreviewRestoresPriorSource()
    {
        SnapshotPublisher publisher = PublisherWithSource("prior-source");
        TreemapSnapshot prior = publisher.Current;

        TwoStageBackend backend = new();
        FileSystemScanner scanner = new(backend, null);
        int faults = 0;
        scanner.PreviewAllocationFault = () =>
            // First preview (one entry) succeeds; the second (two entries) loses
            // its allocation and pauses. Cancellation must still restore the prior.
            Interlocked.Increment(ref faults) == 2
                ? new OutOfMemoryException("simulated second-preview allocation failure")
                : null;

        using CancellationTokenSource cts = new();
        Task<ScanResult> scan = scanner.ScanAsync(
            FakeRoot(nameof(CancelAfterPausedPreviewRestoresPriorSource)), publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot first = await WaitForPreviewAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(first.Root, "first.txt"));

            // Let the backend add the second entry; its preview then OOMs and pauses.
            backend.ContinueSecond.Set();
            Assert.True(backend.AddedSecond.Wait(Timeout), "The backend never added the second entry.");
            await Task.Delay(600);

            cts.Cancel();
            backend.Release.Set();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
            Assert.Same(prior.Root, publisher.Current.Root);
            Assert.Equal("prior-source", publisher.Current.RootPath);
        }
        finally
        {
            backend.Release.Set();
            backend.ContinueSecond.Set();
        }
    }

    private static SnapshotPublisher PublisherWithSource(string source)
        => new(TreemapNode.Create(ScanIdMap.RootId, "empty", NodeKind.Drive), source);

    /// <summary>
    /// Adds one entry, waits for the test to observe its preview, then adds a
    /// second entry and succeeds — so the final tree differs from every preview.
    /// </summary>
    private sealed class StagedBackend(ScanBackend id) : IScanBackend
    {
        public ScanBackend Id { get; } = id;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim ReleaseSecond { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            request.CancellationToken.ThrowIfCancellationRequested();

            Assert.True(collector.TryAdd("first.txt", NodeKind.File, 10, 10, null));
            Entered.Set();
            ReleaseSecond.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(collector.TryAdd("second.txt", NodeKind.File, 20, 20, null));
            return true;
        }
    }

    /// <summary>
    /// Collects nothing until released, so a test can observe the restored prior
    /// map during the portable retry before any new preview can appear.
    /// </summary>
    private sealed class StagedPortableBackend : IScanBackend
    {
        public ScanBackend Id => ScanBackend.Portable;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            Entered.Set();
            Release.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            Assert.True(collector.TryAdd("real.txt", NodeKind.File, 77, 77, null));
            return true;
        }
    }

    /// <summary>
    /// Adds entries, then waits for the test to release it, so previews have a
    /// window to fire mid-scan. Cancellation interrupts the wait.
    /// </summary>
    private sealed class GatedBackend(ScanBackend id) : IScanBackend
    {
        public ScanBackend Id { get; } = id;

        public List<ScanEntry> Entries { get; } = [];

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool Succeeds { get; set; } = true;

        public string? FailureReason { get; set; }

        public Exception? ThrowAfterRelease { get; set; }

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

            if (ThrowAfterRelease is not null)
            {
                throw ThrowAfterRelease;
            }

            failureReason = Succeeds ? null : FailureReason ?? "the gated backend declined";
            return Succeeds;
        }
    }

    /// <summary>
    /// Adds one entry, lets the first preview publish, then adds a second entry so
    /// the next preview is the one under test (OOM-pause), then blocks until
    /// released or cancelled.
    /// </summary>
    private sealed class TwoStageBackend : IScanBackend
    {
        public ScanBackend Id => ScanBackend.Portable;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim ContinueSecond { get; } = new(false);

        public ManualResetEventSlim AddedSecond { get; } = new(false);

        public ManualResetEventSlim Release { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            request.CancellationToken.ThrowIfCancellationRequested();

            Assert.True(collector.TryAdd("first.txt", NodeKind.File, 10, 10, null));
            Entered.Set();

            ContinueSecond.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();

            Assert.True(collector.TryAdd("second.txt", NodeKind.File, 20, 20, null));
            AddedSecond.Set();

            Release.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }
}
