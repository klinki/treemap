using Treemap;
using Treemap.Scanning;
using Treemap.Scanning.Windows;
using Xunit;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Native MFT traversal progress: table position (vacant slots included) across both passes,
/// with a current-pass ETA. Production stream plus backend, no volume handle.
/// </summary>
public class MftProgressTests
{
    private const ulong Root = 5;
    private const ulong Subdirectory = 16;
    private const ushort RootSequence = 5;
    private const ushort RecordSequence = 1;

    [Fact]
    public void FirstPassAdvancesBeforeAnyFileIsEmitted()
    {
        FixtureRecordSource source = VolumeWithBulk(300);
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress);
        collector.SetMftTraversal(MftRecordStream.MftTableBytes(source.Geometry));
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        Assert.True(
            MftRecordStream.TryWalk(source, CancellationToken.None, collector, @"C:\", AcceptAll.Instance, out string? failure,
                ScanPhase.IndexingFolders, 0),
            failure);

        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.NotEmpty(ticks);
        Assert.All(ticks, t => Assert.Equal(ScanPhase.IndexingFolders, t.Phase));
        Assert.All(ticks, t => Assert.NotNull(t.MftTotalBytes));
        Assert.All(ticks, t => Assert.NotNull(t.PercentComplete));
        // No entries were ever added, yet table position moved.
        Assert.Contains(ticks, t => t.EntriesScanned == 0 && (t.MftBytesScanned ?? 0) > 0);
        Assert.All(ticks, t => Assert.True(t.PercentComplete >= 0 && t.PercentComplete <= 50));
        // The move happens before the forced end-of-pass tick: the terminal 50
        // alone must not satisfy the assertion.
        Assert.True(ticks.Length >= 2);
        ScanProgress[] beforeEnd = [.. ticks.Take(ticks.Length - 1)];
        Assert.Contains(beforeEnd, t => (t.MftBytesScanned ?? 0) > 0 && (t.PercentComplete ?? 0) > 0 && (t.PercentComplete ?? 0) < 50);
    }

    [Fact]
    public void VacantGapsCountAsTraversal()
    {
        MftGeometry geometry = new(4096, 4096, 1024, 4096);
        long total = MftRecordStream.MftTableBytes(geometry);

        // Cursor jumped from 4095 to 3499: the 596 skipped ordinals count.
        Assert.Equal(596L * 1024, MftRecordStream.MftBytesTraversed(4095, 3499, 1024, total));

        // Cursor at zero leaves the reserved ordinal itself untraversed: 4095 slots, not 4096.
        Assert.Equal(4095L * 1024, MftRecordStream.MftBytesTraversed(4095, 0, 1024, long.MaxValue));
        // A genuine clamp: movement past a small total saturates at that total.
        Assert.Equal(1_000, MftRecordStream.MftBytesTraversed(4095, 0, 1024, 1_000));
        Assert.Equal(0, MftRecordStream.MftBytesTraversed(10, 20, 1024, total));
    }

    [Fact]
    public void BothPassesAreMonotonicAcrossTheBoundary()
    {
        FixtureRecordSource source = VolumeWithBulk(300);
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress);

        Assert.True(Backend(source).TryRun(Request(@"C:\sub"), collector, out string? failure), failure);

        ScanProgress[] ticks = [.. progress.Ticks];
        ScanProgress[] native = [.. ticks.Where(t => t.MftTotalBytes.HasValue)];
        Assert.Contains(native, t => t.Phase == ScanPhase.IndexingFolders);
        Assert.Contains(native, t => t.Phase == ScanPhase.Enumerating);

        int firstEntryPass = Array.FindIndex(native, t => t.Phase == ScanPhase.Enumerating);
        Assert.True(firstEntryPass > 0);
        Assert.All(native.Take(firstEntryPass + 1), t => Assert.True(t.PercentComplete <= 50));
        Assert.All(native.Skip(firstEntryPass), t => Assert.True(t.PercentComplete >= 50));

        for (int i = 1; i < native.Length; i++)
        {
            Assert.True(native[i].PercentComplete >= native[i - 1].PercentComplete,
                $"percent jumped back at tick {i}: {native[i - 1].PercentComplete} -> {native[i].PercentComplete}");
        }

        Assert.All(native, t => Assert.True(t.PercentComplete <= 99));
    }

    [Fact]
    public void NativeProgressNeedsNoVolumeCapacity()
    {
        FixtureRecordSource source = VolumeWithBulk(10);
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress);
        collector.UsedBytesForProgress = null;

        Assert.True(Backend(source).TryRun(Request(@"C:\sub"), collector, out string? failure), failure);

        ScanProgress[] native = [.. progress.Ticks.Where(t => t.MftTotalBytes.HasValue)];
        Assert.NotEmpty(native);
        Assert.All(native, t => Assert.NotNull(t.PercentComplete));
    }

    [Fact]
    public void CleanEarlyStopNeverReportsTheFullPass()
    {
        FixtureRecordSource source = VolumeWithBulk(300);
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress);
        collector.SetMftTraversal(MftRecordStream.MftTableBytes(source.Geometry));
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        Assert.True(
            MftRecordStream.TryWalk(source, CancellationToken.None, collector, @"C:\", new StopAfter(5), out string? failure,
                ScanPhase.IndexingFolders, 0),
            failure ?? "clean stop returns true");

        long total = MftRecordStream.MftTableBytes(source.Geometry);
        Assert.DoesNotContain(progress.Ticks, t => t.MftBytesScanned == total);
    }

    [Fact]
    public void ErrorAndCancellationNeverReportTheFullPass()
    {
        FixtureRecordSource failing = VolumeWithBulk(300);
        failing.FailAfterReads = 10;
        RecordingProgress errorProgress = new();
        ScanCollector errorCollector = new(TestOptions.Verbose(), errorProgress);
        errorCollector.SetMftTraversal(MftRecordStream.MftTableBytes(failing.Geometry));
        errorCollector.SetBackend(ScanBackend.NtfsMft, null, null);
        errorCollector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        Assert.False(
            MftRecordStream.TryWalk(failing, CancellationToken.None, errorCollector, @"C:\", AcceptAll.Instance, out _,
                ScanPhase.IndexingFolders, 0));
        long total = MftRecordStream.MftTableBytes(failing.Geometry);
        Assert.DoesNotContain(errorProgress.Ticks, t => t.MftBytesScanned == total);

        FixtureRecordSource cancelled = VolumeWithBulk(300);
        ScanCollector cancelCollector = new(TestOptions.Verbose(), new RecordingProgress());
        cancelCollector.SetMftTraversal(MftRecordStream.MftTableBytes(cancelled.Geometry));
        cancelCollector.SetBackend(ScanBackend.NtfsMft, null, null);
        cancelCollector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MftRecordStream.TryWalk(cancelled, cts.Token, cancelCollector, @"C:\", AcceptAll.Instance, out _,
                ScanPhase.IndexingFolders, 0));
    }

    [Fact]
    public void AThrowingProgressCallbackStillThrows()
    {
        FixtureRecordSource source = VolumeWithBulk(10);
        ScanCollector collector = new(TestOptions.Verbose(), new FailingNativeProgress());
        collector.SetMftTraversal(MftRecordStream.MftTableBytes(source.Geometry));
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        IOException error = Assert.Throws<IOException>(() =>
            MftRecordStream.TryWalk(source, CancellationToken.None, collector, @"C:\", AcceptAll.Instance, out _,
                ScanPhase.IndexingFolders, 0));
        Assert.Equal("host blew up", error.Message);
    }

    [Fact]
    public void FallbackClearsTablePositionAndEta()
    {
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress) { UsedBytesForProgress = 7_000 };
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.SetMftTraversal(8_192);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");
        Assert.Contains(progress.Ticks, t => t.MftTotalBytes.HasValue);

        collector.Reset();
        collector.SetBackend(ScanBackend.Portable, ScanBackend.NtfsMft, "MFT unreadable");
        collector.Report(ScanPhase.Enumerating, @"C:\", force: true);

        ScanProgress[] afterFallback = [.. progress.Ticks];
        ScanProgress tick = afterFallback[^1];
        Assert.Null(tick.MftBytesScanned);
        Assert.Null(tick.MftTotalBytes);
        Assert.Null(tick.EstimatedRemainingTime);
        Assert.Equal(0, tick.PercentComplete);
    }

    [Fact]
    public void BuildingHoldsBelowFinishAfterNativeTraversal()
    {
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress);
        collector.SetMftTraversal(8_192);
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.BeginMftPass(1, ScanPhase.Enumerating, @"C:\");

        collector.Report(ScanPhase.Building, @"C:\", force: true);
        collector.Report(ScanPhase.Publishing, null, force: true);
        collector.Report(ScanPhase.Finished, null, force: true);

        ScanProgress[] ticks = [.. progress.Ticks];
        Assert.Equal(99, ticks[^3].PercentComplete);
        Assert.Equal(99, ticks[^2].PercentComplete);
        Assert.Equal(100, ticks[^1].PercentComplete);
        Assert.All(ticks.TakeLast(3), t => Assert.Null(t.MftBytesScanned));
        Assert.All(ticks.TakeLast(3), t => Assert.Null(t.EstimatedRemainingTime));
    }

    [Fact]
    public void ThrottledTicksStillForceAtBoundaries()
    {
        ScanOptions options = TestOptions.Verbose() with { ProgressInterval = TimeSpan.FromHours(1) };
        RecordingProgress progress = new();
        ScanCollector collector = new(options, progress);
        collector.SetMftTraversal(8_192);
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        int afterBegin = progress.Ticks.Count;
        Assert.True(afterBegin > 0);

        collector.ReportMft(ScanPhase.IndexingFolders, @"C:\", 100);
        Assert.Equal(afterBegin, progress.Ticks.Count);

        collector.ReportMft(ScanPhase.IndexingFolders, @"C:\", 100, force: true);
        Assert.Equal(afterBegin + 1, progress.Ticks.Count);
    }

    [Fact]
    public void SwitchingToPortableClearsNativeStateWithoutReset()
    {
        RecordingProgress progress = new();
        ScanCollector collector = new(TestOptions.Verbose(), progress) { UsedBytesForProgress = 7_000 };
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.SetMftTraversal(8_192);
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");
        Assert.Contains(progress.Ticks, t => t.MftTotalBytes.HasValue);

        collector.SetBackend(ScanBackend.Portable, ScanBackend.NtfsMft, "MFT unreadable");
        collector.Report(ScanPhase.Enumerating, @"C:\", force: true);

        ScanProgress[] all = [.. progress.Ticks];
        ScanProgress tick = all[^1];
        Assert.Null(tick.MftBytesScanned);
        Assert.Null(tick.MftTotalBytes);
        Assert.Null(tick.EstimatedRemainingTime);
        Assert.Equal(ScanBackend.Portable, tick.ActiveBackend);
        Assert.Equal(ScanBackend.NtfsMft, tick.FallbackFrom);
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(500, 0.5)]
    [InlineData(999, 0.5)]
    public void EstimateNeedsAboutASecondOfWork(int elapsedMs, double fraction)
    {
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.FromMilliseconds(elapsedMs), fraction));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1)]
    [InlineData(1.5)]
    public void EstimateNeedsPartialProgress(double fraction)
    {
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.FromSeconds(10), fraction));
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.FromSeconds(10), double.NaN));
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.FromSeconds(10), double.PositiveInfinity));
    }

    [Fact]
    public void EstimateScalesWithObservedThroughput()
    {
        // 10s for a quarter of the pass: about 30s left in this pass.
        TimeSpan? remaining = ScanCollector.EstimateMftRemaining(TimeSpan.FromSeconds(10), 0.25);
        Assert.NotNull(remaining);
        Assert.Equal(30, remaining.Value.TotalSeconds, 6);
    }

    [Fact]
    public void EstimateNeverOverflows()
    {
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.MaxValue, double.Epsilon));
        Assert.Null(ScanCollector.EstimateMftRemaining(TimeSpan.FromSeconds(-5), 0.5));
    }

    [Fact]
    public void CancellationFromTheFinalPassCallbackIsObserved()
    {
        FixtureRecordSource source = VolumeWithBulk(10);
        using CancellationTokenSource cancellation = new();
        ScanCollector collector = new(TestOptions.Verbose(), new CancelOnPassEnd(cancellation));
        collector.SetBackend(ScanBackend.NtfsMft, null, null);
        collector.SetMftTraversal(MftRecordStream.MftTableBytes(source.Geometry));
        collector.BeginMftPass(0, ScanPhase.IndexingFolders, @"C:\");

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MftRecordStream.TryWalk(source, cancellation.Token, collector, @"C:\", AcceptAll.Instance, out _,
                ScanPhase.IndexingFolders, 0));
    }

    [Fact]
    public void RemainingTimeAtTheRepresentableBoundaryDoesNotThrow()
    {
        TimeSpan? estimate = ScanCollector.EstimateMftRemaining(TimeSpan.MaxValue, 0.5);
        if (estimate.HasValue)
        {
            Assert.InRange(estimate.Value, TimeSpan.Zero, TimeSpan.MaxValue);
        }
    }

    private sealed class CancelOnPassEnd(CancellationTokenSource cancellation) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            if (value.MftTotalBytes.HasValue && value.MftBytesScanned == value.MftTotalBytes)
            {
                cancellation.Cancel();
            }
        }
    }
    private static NtfsMftScanBackend Backend(FixtureRecordSource source)
        => new(_ => source, static (string path, out long logical, out long allocated, out DateTimeOffset? lastWrite) =>
        {
            logical = 0;
            allocated = 0;
            lastWrite = null;
            return false;
        });

    private static ScanRequest Request(string rootPath)
        => new(rootPath, TestOptions.Verbose(), CancellationToken.None);

    private static FixtureRecordSource VolumeWithBulk(int files)
    {
        FixtureRecordSource source = new(new MftGeometry(4096, 4096, 1024, 4096));
        source.Add(Root, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RootSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Root, ".", MftNameNamespace.Posix)
            .Build());
        source.Add(Subdirectory, new MftRecordBuilder()
            .AsDirectory()
            .Sequence(RecordSequence)
            .AddStandardInformation(FileAttributes.Directory)
            .AddFileName(Root, "sub", parentSequence: RootSequence)
            .Build());

        for (int i = 0; i < files; i++)
        {
            ulong ordinal = (ulong)(100 + i);
            source.Add(ordinal, new MftRecordBuilder()
                .Sequence(RecordSequence)
                .AddStandardInformation(FileAttributes.Normal)
                .AddFileName(Subdirectory, $"bulk{i:000}.txt")
                .AddDataNonResident(dataSize: 1_024, allocatedSize: 1_024)
                .Build());
        }

        return source;
    }

    private sealed class AcceptAll : IMftRecordVisitor
    {
        public static AcceptAll Instance { get; } = new();

        public string? FailureReason => null;

        public bool Visit(MftRecordInfo record) => true;
    }

    private sealed class StopAfter(int limit) : IMftRecordVisitor
    {
        private int _seen;


        public string? FailureReason => null;

        public bool Visit(MftRecordInfo record) => ++_seen < limit;
    }

    private sealed class FailingNativeProgress : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            // Throw only once the walk has moved: the pass-start tick at zero must
            // succeed so the throw is exercised from inside the walk, not the arrange.
            if (value.MftTotalBytes.HasValue && (value.MftBytesScanned ?? 0) > 0)
            {
                throw new IOException("host blew up");
            }
        }
    }
}
