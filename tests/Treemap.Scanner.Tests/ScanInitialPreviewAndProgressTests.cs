using System.Diagnostics;
using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Initial volume preview before any discovery, and byte progress for the progress bar.
/// Backends are gated so the initial basis must appear before enumeration finishes.
/// </summary>
public class ScanInitialPreviewAndProgressTests
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static string VolumeRoot => OperatingSystem.IsWindows() ? @"C:\" : "/";

    private static ScanOptions PreviewOptions(ScanBackendPreference backend = ScanBackendPreference.Portable) => new()
    {
        Backend = backend,
        ProgressInterval = TimeSpan.Zero,
        PublishIntermediateSnapshots = true,
        SnapshotInterval = PreviewInterval,
    };

    private static string FakeRoot(string name)
        => Path.Combine(Path.GetTempPath(), "treemap-initial-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);

    private static FileSystemScanner VolumeScanner(IScanBackend portable, long total, long free) => new(
        portable,
        null,
        _ => new VolumeSpaceInfo(total, free));

    private static async Task<TreemapSnapshot> WaitForVersionAsync(SnapshotPublisher publisher, long afterVersion)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            TreemapSnapshot current = publisher.Current;
            if (current.Version > afterVersion)
            {
                return current;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No publish arrived before the timeout.");
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

    [Fact]
    public async Task InitialBasisPublishedBeforeAnyDiscovery_WithStableIdsAndDecreasingResidual()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        TwoStageVolumeBackend backend = new(ScanBackend.Portable);

        FileSystemScanner scanner = VolumeScanner(backend, total: 10_000, free: 3_000);
        string root = VolumeRoot;
        Task<ScanResult> scan = scanner.ScanAsync(root, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");

            // Gate 1: nothing discovered yet, but the initial basis is visible.
            TreemapSnapshot initial = await WaitForVersionAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(initial.Root, "Free space"));
            Assert.NotNull(TreeAssert.Find(initial.Root, "Unaccounted space"));
            Assert.Null(TreeAssert.Find(initial.Root, "found.bin"));
            Assert.Equal(3_000, TreeAssert.Find(initial.Root, "Free space")!.PhysicalSize);
            Assert.Equal(7_000, TreeAssert.Find(initial.Root, "Unaccounted space")!.PhysicalSize);

            int freeId = TreeAssert.Find(initial.Root, "Free space")!.Id;
            int unknownId = TreeAssert.Find(initial.Root, "Unaccounted space")!.Id;

            // No repeated empty previews while discovery is still gated.
            long versionAfterInitial = publisher.Current.Version;
            await Task.Delay(200);
            Assert.Equal(versionAfterInitial, publisher.Current.Version);

            // Gate 2: the file is found but the backend has not completed. The timer
            // preview replaces the residual with the discovered entry on the same basis.
            backend.ReleaseDiscovery.Set();
            Assert.True(backend.Discovered.Wait(Timeout), "The backend never reported its entry.");

            TreemapSnapshot preview = await WaitForNodeAsync(publisher, "found.bin");
            Assert.Equal(3_000, TreeAssert.Find(preview.Root, "Free space")!.PhysicalSize);
            Assert.Equal(5_000, TreeAssert.Find(preview.Root, "Unaccounted space")!.PhysicalSize);
            Assert.Equal(freeId, TreeAssert.Find(preview.Root, "Free space")!.Id);
            Assert.Equal(unknownId, TreeAssert.Find(preview.Root, "Unaccounted space")!.Id);

            backend.ReleaseCompletion.Set();
            ScanResult result = await scan;

            // The final tree measures against the same cached basis with stable ids.
            Assert.Equal(3_000, result.FreeSpaceBytes);
            Assert.Equal(5_000, result.UnknownSpaceBytes);
            Assert.Equal(freeId, TreeAssert.Find(result.Root, "Free space")!.Id);
            Assert.Equal(unknownId, TreeAssert.Find(result.Root, "Unaccounted space")!.Id);
            Assert.NotNull(TreeAssert.Find(result.Root, "found.bin"));
            Assert.Same(result.Root, publisher.Current.Root);
        }
        finally
        {
            backend.ReleaseDiscovery.Set();
            backend.ReleaseCompletion.Set();
            try
            {
                await scan.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task CancellationAfterInitialRestoresExactPriorSource()
    {
        SnapshotPublisher publisher = new(TreemapNode.Create(ScanIdMap.RootId, "empty", NodeKind.Drive), "prior-source");
        TreemapSnapshot prior = publisher.Current;

        VolumeGatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("found.bin", NodeKind.File, 1_000, 1_000, null));

        using CancellationTokenSource cts = new();
        Task<ScanResult> scan = VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, publisher, PreviewOptions(), cancellationToken: cts.Token);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            TreemapSnapshot initial = await WaitForVersionAsync(publisher, prior.Version);
            Assert.NotEqual("prior-source", initial.RootPath);

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
    public async Task BackendFailureAfterInitialRestoresExactPriorSource()
    {
        SnapshotPublisher publisher = new(TreemapNode.Create(ScanIdMap.RootId, "empty", NodeKind.Drive), "prior-source");
        TreemapSnapshot prior = publisher.Current;

        VolumeGatedBackend backend = new(ScanBackend.Portable)
        {
            ThrowAfterRelease = new IOException("volume backend broke"),
        };
        backend.Entries.Add(new ScanEntry("found.bin", NodeKind.File, 1_000, 1_000, null));

        Task<ScanResult> scan = VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await WaitForVersionAsync(publisher, prior.Version);

            backend.Release.Set();
            IOException error = await Assert.ThrowsAsync<IOException>(() => scan);
            Assert.Equal("volume backend broke", error.Message);
            Assert.Same(prior.Root, publisher.Current.Root);
            Assert.Equal("prior-source", publisher.Current.RootPath);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task InitialSubscriberFailureRestoresPriorAndAbortsIdentities()
    {
        SnapshotPublisher publisher = new(TreemapNode.Create(ScanIdMap.RootId, "empty", NodeKind.Drive), "prior-source");
        TreemapSnapshot prior = publisher.Current;
        publisher.Published += () =>
        {
            if (publisher.Current.RootPath is not null && publisher.Current.RootPath != "prior-source")
            {
                throw new IOException("initial subscriber blew up");
            }
        };

        VolumeGatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 10, 10, null));

        FileSystemScanner scanner = VolumeScanner(backend, total: 10_000, free: 3_000);
        IOException error = await Assert.ThrowsAsync<IOException>(
            () => scanner.ScanAsync(VolumeRoot, publisher, PreviewOptions()));

        Assert.Equal("initial subscriber blew up", error.Message);
        Assert.Same(prior.Root, publisher.Current.Root);
        Assert.Equal("prior-source", publisher.Current.RootPath);
        Assert.False(backend.Entered.Wait(300), "The backend must never run when the initial publish fails.");

        // Identity was aborted, not committed: the next scan still builds.
        ScriptedBackend retry = new(ScanBackend.Portable);
        retry.Entries.Add(new ScanEntry("b.bin", NodeKind.File, 5, 5, null));
        SnapshotPublisher secondPublisher = TestPublisher.Empty();
        ScanResult second = await VolumeScanner(retry, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, secondPublisher, TestOptions.Verbose());
        Assert.NotNull(TreeAssert.Find(second.Root, "b.bin"));
    }

    [Fact]
    public async Task FolderScanPublishesNoInitialBasis_AndNeverQueriesCapacity()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        VolumeGatedBackend backend = new(ScanBackend.Portable);

        int providerCalls = 0;
        RecordingProgress progress = new();
        ScanOptions options = PreviewOptions() with { ProgressInterval = TimeSpan.Zero };

        Task<ScanResult> scan = new FileSystemScanner(
                backend,
                null,
                _ => { Interlocked.Increment(ref providerCalls); return new VolumeSpaceInfo(10_000, 3_000); })
            .ScanAsync(FakeRoot(nameof(FolderScanPublishesNoInitialBasis_AndNeverQueriesCapacity)), publisher, options, progress);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(0, providerCalls);
            Assert.Equal(0, result.FreeSpaceBytes);
            Assert.DoesNotContain(result.Root.Children, c => c.IsSpace);
            var folderTicks = progress.Ticks;
            Assert.True(folderTicks.Count > 0);
            Assert.All(folderTicks.Take(folderTicks.Count - 1), t => Assert.Null(t.PercentComplete));
            Assert.Equal(100, folderTicks[folderTicks.Count - 1].PercentComplete);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task FinalOnlyVolumeScanPublishesNothingMidScan()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        VolumeGatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        Task<ScanResult> scan = VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, publisher, TestOptions.Verbose());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(1, result.FileCount);
            Assert.Equal(3_000, result.FreeSpaceBytes);
            Assert.Equal(1, publisher.Current.Version);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task UnavailableCapacityPublishesNoInitialBasis()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        VolumeGatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        Task<ScanResult> scan = new FileSystemScanner(backend, null, _ => null)
            .ScanAsync(VolumeRoot, publisher, PreviewOptions());

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(0, result.FreeSpaceBytes);
            Assert.Equal(0, result.UnknownSpaceBytes);
            Assert.Contains(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Warning);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task DisabledSpaceBlocksPublishNoInitialBasis()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        VolumeGatedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        ScanOptions options = PreviewOptions() with { IncludeFreeSpace = false, IncludeUnknownSpace = false };
        Task<ScanResult> scan = VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, publisher, options);

        try
        {
            Assert.True(backend.Entered.Wait(Timeout), "The backend never started.");
            await Task.Delay(300);
            Assert.Equal(0, publisher.Current.Version);

            backend.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(0, result.FreeSpaceBytes);
            Assert.Equal(0, result.UnknownSpaceBytes);
        }
        finally
        {
            backend.Release.Set();
        }
    }

    [Fact]
    public async Task CapacityIsReadOnce_ForInitialPreviewsFinalAndProgress()
    {
        int providerCalls = 0;
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        SnapshotPublisher publisher = TestPublisher.Empty();
        RecordingProgress progress = new();
        ScanOptions options = PreviewOptions() with { ProgressInterval = TimeSpan.Zero };

        ScanResult result = await new FileSystemScanner(
                backend,
                null,
                _ => { Interlocked.Increment(ref providerCalls); return new VolumeSpaceInfo(10_000, 3_000); })
            .ScanAsync(VolumeRoot, publisher, options, progress);

        Assert.Equal(1, providerCalls);
        Assert.Equal(3_000, result.FreeSpaceBytes);
        Assert.Contains(progress.Ticks, t => t.PercentComplete is not null && t.PercentComplete < 100);
        ScanProgress[] capTicks = [.. progress.Ticks];
        Assert.Equal(100, capTicks[^1].PercentComplete);
    }

    [Fact]
    public async Task KnownVolumeProgressIsRepresentedOverUsed_ClampedBelowFinish()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("half.bin", NodeKind.File, 3_500, 3_500, null));

        RecordingProgress progress = new();
        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: progress);

        Assert.Equal(3_500, result.TotalPhysicalSize);
        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.Equal(0, ticks[0].PercentComplete);
        Assert.All(ticks.Take(ticks.Length - 1), t => Assert.True(t.PercentComplete >= 0 && t.PercentComplete <= 99));
        Assert.Contains(ticks, t => t.Phase == ScanPhase.Building && t.PercentComplete == 50);
        Assert.Equal(100, ticks[^1].PercentComplete);
        Assert.Equal(ScanPhase.Finished, ticks[^1].Phase);
    }

    [Fact]
    public async Task OvercountedProgressClampsTo99UntilFinished()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("huge.bin", NodeKind.File, 99_999, 99_999, null));

        RecordingProgress progress = new();
        await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: progress);

        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.All(ticks.Take(ticks.Length - 1), t => Assert.True(t.PercentComplete <= 99));
        Assert.Contains(ticks, t => t.Phase == ScanPhase.Building && t.PercentComplete == 99);
        Assert.Equal(100, ticks[^1].PercentComplete);
    }

    [Fact]
    public async Task MissingAllocationFallsBackToLogicalForProgress()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("sparse.bin", NodeKind.File, 3_500, 0, null));

        RecordingProgress progress = new();
        ScanResult result = await VolumeScanner(backend, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: progress);

        Assert.Equal(3_500, result.TotalLogicalSize);
        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.All(ticks.Take(ticks.Length - 1), t => Assert.True(t.PercentComplete >= 0 && t.PercentComplete <= 99));
        Assert.Contains(ticks, t => t.Phase == ScanPhase.Building && t.PercentComplete == 50);
        Assert.Equal(100, ticks[^1].PercentComplete);
    }

    [Fact]
    public async Task ClampedBasisAndExtremeRepresentedStayBounded()
    {
        // Free beyond the total clamps the used total to zero: 0 until Finished.
        ScriptedBackend clamped = new(ScanBackend.Portable);
        clamped.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        RecordingProgress clampedProgress = new();
        await VolumeScanner(clamped, total: 10_000, free: 15_000)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: clampedProgress);

        ScanProgress[] clampedTicks = [.. clampedProgress.Ticks];
        Assert.All(clampedTicks.Take(clampedTicks.Length - 1), t => Assert.Equal(0, t.PercentComplete));
        Assert.Equal(100, clampedTicks[^1].PercentComplete);

        // A represented total near long.MaxValue clamps to 99 without overflow or NaN.
        ScriptedBackend huge = new(ScanBackend.Portable);
        huge.Entries.Add(new ScanEntry("huge.bin", NodeKind.File, 1_000, long.MaxValue, null));

        RecordingProgress hugeProgress = new();
        await VolumeScanner(huge, total: 10_000, free: 3_000)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: hugeProgress);

        ScanProgress[] hugeTicks = [.. hugeProgress.Ticks];
        Assert.All(hugeTicks, t => Assert.True(t.PercentComplete.HasValue));
        Assert.All(hugeTicks.Take(hugeTicks.Length - 1), t => Assert.True(t.PercentComplete >= 0 && t.PercentComplete <= 99));
        Assert.Contains(hugeTicks, t => t.PercentComplete == 99);
        Assert.Equal(100, hugeTicks[^1].PercentComplete);
    }

    [Fact]
    public async Task ZeroTotalProgressIsZeroUntilFinished()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        RecordingProgress progress = new();
        await VolumeScanner(backend, total: 0, free: 0)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: progress);

        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.All(ticks.Take(ticks.Length - 1), t => Assert.Equal(0, t.PercentComplete));
        Assert.Equal(100, ticks[^1].PercentComplete);
    }

    [Fact]
    public async Task UnavailableCapacityProgressIsNullUntilFinished()
    {
        ScriptedBackend backend = new(ScanBackend.Portable);
        backend.Entries.Add(new ScanEntry("a.bin", NodeKind.File, 1_000, 1_000, null));

        RecordingProgress progress = new();
        await new FileSystemScanner(backend, null, _ => null)
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(), progress: progress);

        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.All(ticks.Take(ticks.Length - 1), t => Assert.Null(t.PercentComplete));
        Assert.Equal(100, ticks[^1].PercentComplete);
    }

    [Fact]
    public async Task FallbackResetsByteProgressToZero()
    {
        ReportingDecliningBackend fast = new();
        ScriptedBackend portable = new(ScanBackend.Portable);
        portable.Entries.Add(new ScanEntry("real.bin", NodeKind.File, 1_000, 1_000, null));

        RecordingProgress progress = new();
        ScanResult result = await new FileSystemScanner(portable, fast, _ => new VolumeSpaceInfo(10_000, 3_000))
            .ScanAsync(VolumeRoot, options: TestOptions.Verbose(ScanBackendPreference.Auto), progress: progress);

        Assert.Equal(ScanBackend.Portable, result.Backend);
        ScanProgress[] ticks = [.. progress.Ticks];

        // Fast path reported ~71% (5000/7000), then the forced Enumerating tick
        // after the abandon runs on a reset collector, back at 0.
        int fastIndex = Array.FindIndex(ticks, t => t.PercentComplete == 71);
        Assert.True(fastIndex >= 0, "Expected a 71% tick from the abandoned backend.");
        Assert.Contains(ticks.Skip(fastIndex + 1), t => t.Phase == ScanPhase.Enumerating && t.PercentComplete == 0);
        Assert.Equal(100, ticks[^1].PercentComplete);
    }

    [Fact]
    public async Task FallbackRepublishesInitialBasisForPortableRetry()
    {
        SnapshotPublisher publisher = TestPublisher.Empty();
        TreemapSnapshot prior = publisher.Current;

        VolumeGatedBackend fast = new(ScanBackend.NtfsMft);
        fast.Entries.Add(new ScanEntry("ghost.bin", NodeKind.File, 5_000, 5_000, null));
        fast.Succeeds = false;
        fast.FailureReason = "MFT unreadable";

        StagedVolumePortableBackend portable = new();

        Task<ScanResult> scan = new FileSystemScanner(portable, fast, _ => new VolumeSpaceInfo(10_000, 3_000))
            .ScanAsync(VolumeRoot, publisher, PreviewOptions(ScanBackendPreference.Auto));

        try
        {
            Assert.True(fast.Entered.Wait(Timeout), "The fast backend never started.");
            TreemapSnapshot initial = await WaitForVersionAsync(publisher, prior.Version);
            Assert.NotNull(TreeAssert.Find(initial.Root, "Unaccounted space"));

            fast.Release.Set();
            Assert.True(portable.Entered.Wait(Timeout), "The portable backend never started.");

            // The abandon republished the initial basis: space blocks visible, ghost gone.
            Stopwatch clock = Stopwatch.StartNew();
            TreemapSnapshot republished = publisher.Current;
            while (clock.Elapsed < Timeout)
            {
                republished = publisher.Current;
                if (TreeAssert.Find(republished.Root, "Unaccounted space") is not null
                    && TreeAssert.Find(republished.Root, "ghost.bin") is null)
                {
                    break;
                }

                await Task.Delay(10);
            }

            Assert.NotNull(TreeAssert.Find(republished.Root, "Unaccounted space"));
            Assert.Null(TreeAssert.Find(republished.Root, "ghost.bin"));
            int republishedUnknownId = TreeAssert.Find(republished.Root, "Unaccounted space")!.Id;

            portable.Release.Set();
            ScanResult result = await scan;

            Assert.Equal(ScanBackend.Portable, result.Backend);
            Assert.Null(TreeAssert.Find(result.Root, "ghost.bin"));
            Assert.NotNull(TreeAssert.Find(result.Root, "real.bin"));
            Assert.Equal(republishedUnknownId, TreeAssert.Find(result.Root, "Unaccounted space")!.Id);
        }
        finally
        {
            fast.Release.Set();
            portable.Release.Set();
        }
    }

    [Fact]
    public void PositionalConstructorLeavesPercentUnknown()
    {
        ScanProgress tick = new(ScanPhase.Enumerating, "path", 1, 2);
        Assert.Null(tick.PercentComplete);
        Assert.Equal(100, (tick with { PercentComplete = 100 }).PercentComplete);
    }

    /// <summary>
    /// Two bounded gates: waits before any discovery so the initial basis is
    /// observable, then waits after adding the entry so the timer preview is
    /// observable before the backend completes.
    /// </summary>
    private sealed class TwoStageVolumeBackend(ScanBackend id) : IScanBackend
    {
        public ScanBackend Id { get; } = id;

        public ManualResetEventSlim Entered { get; } = new(false);

        public ManualResetEventSlim Discovered { get; } = new(false);

        public ManualResetEventSlim ReleaseDiscovery { get; } = new(false);

        public ManualResetEventSlim ReleaseCompletion { get; } = new(false);

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            failureReason = null;
            request.CancellationToken.ThrowIfCancellationRequested();
            Entered.Set();

            ReleaseDiscovery.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();

            Assert.True(collector.TryAdd("found.bin", NodeKind.File, 2_000, 2_000, null));
            Discovered.Set();

            ReleaseCompletion.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            request.CancellationToken.ThrowIfCancellationRequested();
            return true;
        }
    }

    /// <summary>
    /// Blocks before collecting, so the test observes the initial basis while the
    /// entry set is still empty. Collects only after release.
    /// </summary>
    private sealed class VolumeGatedBackend(ScanBackend id) : IScanBackend
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
            Entered.Set();

            try
            {
                Release.Wait(System.Threading.Timeout.InfiniteTimeSpan, request.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }

            request.CancellationToken.ThrowIfCancellationRequested();

            if (ThrowAfterRelease is not null)
            {
                throw ThrowAfterRelease;
            }

            foreach (ScanEntry entry in Entries)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                if (!collector.TryAdd(entry.RelativePath, entry.Kind, entry.LogicalSize, entry.PhysicalSize, entry.LastWriteTime))
                {
                    break;
                }
            }

            failureReason = Succeeds ? null : FailureReason ?? "the gated backend declined";
            return Succeeds;
        }
    }

    /// <summary>
    /// Collects nothing until released, so the fallback republish is observable
    /// before any portable entry can appear.
    /// </summary>
    private sealed class StagedVolumePortableBackend : IScanBackend
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
            Assert.True(collector.TryAdd("real.bin", NodeKind.File, 1_000, 1_000, null));
            return true;
        }
    }

    /// <summary>
    /// Adds a large partial, reports it, then declines so the portable retry runs.
    /// </summary>
    private sealed class ReportingDecliningBackend : IScanBackend
    {
        public ScanBackend Id => ScanBackend.NtfsMft;

        public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
        {
            Assert.True(collector.TryAdd("partial.bin", NodeKind.File, 5_000, 5_000, null));
            collector.Report(ScanPhase.Enumerating, "partial", force: true);
            failureReason = "MFT unreadable";
            return false;
        }
    }
}
