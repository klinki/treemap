using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the scan progress row: full-width directly below the toolbar rows with
/// the filter row beneath it, the bar filling the row with a whole-percentage label
/// beside it, determinate only while the scan reports a discovery estimate, marquee
/// with an honest placeholder otherwise, 100 on every accepted finish including
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
        Assert.Equal("0%", shell.PercentText);

        // Full available width: the row is docked top, so it spans the client width.
        Assert.Equal(DockStyle.Top, shell.ProgressRow.Dock);
        Assert.Equal(shell.ClientSize.Width, shell.ProgressRow.Width);

        // The bar fills the row with the percentage beside it, in the same row.
        Assert.Same(shell.ProgressRow, shell.ProgressBarControl.Parent);
        Assert.Same(shell.ProgressRow, shell.PercentLabel.Parent);
        Assert.Equal(DockStyle.Fill, shell.ProgressBarControl.Dock);
        // TableLayout cell alignment, not a docked panel: the label anchors right
        // inside its auto-sized column; adjacency and bounds below are the behavior.
        Assert.Equal(DockStyle.None, shell.PercentLabel.Dock);
        Assert.True(shell.PercentLabel.Anchor.HasFlag(AnchorStyles.Right),
            "the percentage label is not anchored right");
        Assert.True(shell.ProgressBarControl.Width > 0, "the bar has no width beside the label");
        Assert.True(shell.PercentLabel.Left >= shell.ProgressBarControl.Right,
            "the percentage label overlaps the bar");
        Assert.True(shell.PercentLabel.Bottom <= shell.ProgressRow.Height);
        Assert.True(shell.PercentLabel.Right <= shell.ProgressRow.Width);

        // Directly below the toolbar rows with the filter row beneath it, above the view.
        Assert.True(shell.ProgressRow.Top < shell.FilterRow.Top);
        Assert.True(shell.ProgressRow.Bottom <= shell.FilterRow.Top);
        Assert.True(shell.FilterRow.Bottom <= shell.View.Top);

        // The row sizes to its content, so the label still fits at the smallest
        // window and under a larger font instead of clipping on a fixed height.
        shell.Size = shell.MinimumSize;
        shell.PerformLayout();
        Assert.True(shell.PercentLabel.Bottom <= shell.ProgressRow.Height);
        Assert.True(shell.PercentLabel.Right <= shell.ProgressRow.Width);
        Assert.True(shell.PercentLabel.Left >= shell.ProgressBarControl.Right,
            "the percentage label overlaps the bar at the smallest window");

        using Font larger = new(shell.Font.FontFamily, shell.Font.Size + 6f);
        shell.Font = (Font)larger.Clone();
        shell.PerformLayout();
        Assert.True(shell.PercentLabel.Bottom <= shell.ProgressRow.Height);
        Assert.True(shell.PercentLabel.Right <= shell.ProgressRow.Width);
        Assert.True(shell.PercentLabel.Left >= shell.ProgressBarControl.Right,
            "the percentage label overlaps the bar under a larger font");
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

        Assert.Equal("42%", shell.PercentText);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Equal(ProgressBarStyle.Continuous, shell.ProgressBarControl.Style);
        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal("100%", shell.PercentText);
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

        // No estimate, no fake number: the placeholder shows while marquee runs.
        Assert.Equal("...", shell.PercentText);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal("100%", shell.PercentText);
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
        Assert.Equal("100%", shell.PercentText);
        Assert.Contains("Incomplete", shell.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public void ANewScanRestartsTheBarAtZeroMarquee() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim reportTick = new();
        using ScanShell shell = CreateShell(scanRunner: async (
            rootPath, publisher, options, progress, token) =>
        {
            entered.Set();

            // Held until the test has seen the initial reset: the reset marquee
            // (0%) and an unknown-estimate marquee (...) are the same style, so
            // reporting immediately would race the initial assertion.
            Assert.True(reportTick.Wait(WorkerDeadline), "the test never released the tick");
            progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 12, 4096));
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("the injected scan was never cancelled");
        });

        shell.StartScan(@"C:\Windows");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");
        StaRunner.PumpUntil(() => shell.ProgressBarControl.Style == ProgressBarStyle.Marquee);

        // Nothing is known yet: the bar is marquee over a zero model with 0%.
        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.Equal("0%", shell.PercentText);

        // The first estimate is unknown, so no fake number replaces the reset:
        // the placeholder shows while marquee keeps running.
        reportTick.Set();
        StaRunner.PumpUntil(() => shell.PercentText == "...");

        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.Equal(ProgressBarStyle.Marquee, shell.ProgressBarControl.Style);
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

        Assert.Equal("99%", shell.PercentText);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal("100%", shell.PercentText);
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
        Assert.Equal("0%", shell.PercentText);
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
        Assert.Equal("0%", shell.PercentText);
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
        Assert.Equal("100%", shell.PercentText);

        // A tick produced by the first scan, delivered after both scans finished.
        fromFirstScan!.Report(new ScanProgress(ScanPhase.Enumerating, @"C:\first", 9999, 9999) { PercentComplete = 5 });
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal("100%", shell.PercentText);
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
