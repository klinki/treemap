using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the scan progress bar: full-width directly below the toolbar rows with
/// the filter row beneath it, determinate only while the scan reports a discovery
/// estimate, marquee from zero otherwise, 100 on every accepted finish including
/// an incomplete one, zero only on cancel or failure, and stale ticks dropped.
/// </summary>
public class ProgressBarTests
{
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    private static readonly ScanOptions PortableOnly = ScanOptions.Default with
    {
        Backend = ScanBackendPreference.Portable,
        ProgressInterval = TimeSpan.Zero,
    };

    [Fact]
    public void TheBarStartsAtZeroAndSitsBelowTheToolbars() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell();
        shell.PerformLayout();

        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);

        // Full available width: docked top, so it spans the client width.
        Assert.Equal(DockStyle.Top, shell.ProgressBarControl.Dock);
        Assert.Equal(shell.ClientSize.Width, shell.ProgressBarControl.Width);

        // Directly below the toolbar rows with the filter row beneath it, above the view.
        Assert.True(shell.ProgressBarControl.Top < shell.FilterRow.Top);
        Assert.True(shell.ProgressBarControl.Bottom <= shell.FilterRow.Top);
        Assert.True(shell.FilterRow.Bottom <= shell.View.Top);
    });

    [Fact]
    public void AnEstimateTickShowsDeterminateAndACompleteFinishStopsAtFull() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        TreemapNode root = FilterTrees.Root();

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            entered.Set();
            progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 10, 100) { PercentComplete = 42 });
            Assert.True(release.Wait(WorkerDeadline), "the test never released the scan");
            token.ThrowIfCancellationRequested();
            publisher.Publish(root);
            return Task.FromResult(CompleteResult(rootPath, root));
        });

        shell.StartScan(@"C:\");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Continuous
            && shell.ProgressBarControl.Value == 42);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);
        Assert.Equal(100, shell.ProgressBarControl.Value);
    });

    [Fact]
    public void ATickWithoutAnEstimateRunsMarqueeWhileScanning() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        TreemapNode root = FilterTrees.Root();

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            entered.Set();
            progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 10, 100));
            Assert.True(release.Wait(WorkerDeadline), "the test never released the scan");
            token.ThrowIfCancellationRequested();
            publisher.Publish(root);
            return Task.FromResult(CompleteResult(rootPath, root));
        });

        shell.StartScan(@"C:\");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Marquee);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);
    });

    [Fact]
    public void AnIncompleteFinishStillStopsAtFullWithTheWarningKept() => StaRunner.Run(() =>
    {
        TreemapNode root = FilterTrees.Root();
        ScanResult result = CompleteResult(@"C:\", root) with { IsComplete = false, IncompleteReason = "part missing" };

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            publisher.Publish(root);
            return Task.FromResult(result);
        });

        shell.StartScan(@"C:\");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        // An accepted finish is 100 consistently with ScanProgress.Finished,
        // complete or not; the warning and the residual block stay elsewhere.
        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);
        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Contains("Incomplete", shell.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public void ANewScanRestartsTheBarAtZeroMarquee() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ScanShell shell = CreateShell(scanRunner: BlockingScan(entered));

        shell.StartScan(@"C:\Windows");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");
        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Marquee);

        Assert.Equal(0, shell.ProgressBarControl.Value);
    });

    [Fact]
    public void ANonFinishedTickClaimingFullIsClampedBelowCompletion() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        TreemapNode root = FilterTrees.Root();

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            entered.Set();
            progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 10, 100) { PercentComplete = 100 });
            Assert.True(release.Wait(WorkerDeadline), "the test never released the scan");
            token.ThrowIfCancellationRequested();
            publisher.Publish(root);
            return Task.FromResult(CompleteResult(rootPath, root));
        });

        shell.StartScan(@"C:\");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

        // The 0..99 estimate contract: only Finished owns 100.
        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Continuous
            && shell.ProgressBarControl.Value == 99);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);
    });

    [Fact]
    public void CancelAndFailureResetToZero() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ScanShell shell = CreateShell(scanRunner: BlockingScan(entered));

        shell.StartScan(@"C:\Windows");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");
        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Marquee);

        shell.CancelScan();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);
    });

    [Fact]
    public void AFailedScanResetsToZero() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
            Task.FromException<ScanResult>(new DirectoryNotFoundException("gone")));

        shell.StartScan(@"C:\not-here");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);
    });

    [Fact]
    public void AStaleTickNeverOverwritesTheAcceptedValue() => StaRunner.Run(() =>
    {
        int runs = 0;
        IProgress<ScanProgress>? fromFirstScan = null;
        TreemapNode root = FilterTrees.Root();

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            bool first = Interlocked.Increment(ref runs) == 1;
            if (first)
            {
                fromFirstScan = progress;
            }

            publisher.Publish(root);
            return Task.FromResult(CompleteResult(rootPath, root));
        });

        shell.StartScan(@"C:\first");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        shell.StartScan(@"C:\second");
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);

        // A tick produced by the first scan, delivered after both scans finished.
        fromFirstScan!.Report(new ScanProgress(ScanPhase.Enumerating, @"C:\first", 9999, 9999) { PercentComplete = 5 });
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal(string.Empty, shell.ProgressText);
    });

    // ---- helpers ----

    private static ScanResult CompleteResult(string rootPath, TreemapNode root) => new()
    {
        RootPath = rootPath,
        Root = root,
        Backend = ScanBackend.Portable,
        IsComplete = true,
        FileCount = 3,
        Duration = TimeSpan.FromMilliseconds(10),
    };

    private static ScanRunner BlockingScan(ManualResetEventSlim entered)
    {
        async Task<ScanResult> Run(
            string rootPath,
            SnapshotPublisher publisher,
            ScanOptions options,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken)
        {
            entered.Set();
            progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 12, 4096));
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("the injected scan was never cancelled");
        }

        return Run;
    }

    private static ScanShell CreateShell(ScanRunner? scanRunner = null)
    {
        ScanShell shell = new(scanRunner, PortableOnly);
        _ = shell.Handle;
        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        return shell;
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;

            foreach (Control nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
