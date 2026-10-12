using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Native MFT progress display: phase names, table size and approximate current-pass time
/// in the progress line, with the percentage label matching the bar and stale details
/// cleared on later phases.
/// </summary>
public class MftProgressDisplayTests
{
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void IndexingPhaseHasItsOwnName()
    {
        Assert.Equal("Indexing folders", ScanShell.DescribePhase(ScanPhase.IndexingFolders));
        Assert.Equal("Reading files", ScanShell.DescribePhase(ScanPhase.Enumerating));
    }

    [Fact]
    public void TraversalTextShowsTableSizeAndApproxTime()
    {
        ScanProgress tick = new(ScanPhase.IndexingFolders, @"C:\", 0, 0)
        {
            MftBytesScanned = 120_000_000,
            MftTotalBytes = 2_000_000_000,
            EstimatedRemainingTime = TimeSpan.FromSeconds(30),
        };

        string text = ScanShell.DescribeMftTraversal(tick);
        Assert.Contains("MFT", text, StringComparison.Ordinal);
        Assert.Contains("of", text, StringComparison.Ordinal);
        Assert.Contains("about 30s left in this pass", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TraversalTextIsAbsentWithoutTablePositionOrOutsideTheWalks()
    {
        ScanProgress portable = new(ScanPhase.Enumerating, @"C:\", 1, 2);
        Assert.Equal(string.Empty, ScanShell.DescribeMftTraversal(portable));

        ScanProgress building = new(ScanPhase.Building, @"C:\", 1, 2)
        {
            MftBytesScanned = 10,
            MftTotalBytes = 100,
            EstimatedRemainingTime = TimeSpan.FromSeconds(5),
        };
        Assert.Equal(string.Empty, ScanShell.DescribeMftTraversal(building));
    }

    [Fact]
    public void RemainingTimeIsCoarse()
    {
        Assert.Equal("30s", ScanShell.FormatRemaining(TimeSpan.FromSeconds(30)));
        Assert.Equal("2m", ScanShell.FormatRemaining(TimeSpan.FromSeconds(90)));
        Assert.Equal("2h", ScanShell.FormatRemaining(TimeSpan.FromHours(1.7)));
    }

    [Fact]
    public void ANativeTickShowsSizeAndTimeWithAMatchingPercent() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        TreemapNode root = FilterTrees.Root();

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            entered.Set();
            progress.Report(new ScanProgress(ScanPhase.IndexingFolders, rootPath, 0, 0)
            {
                PercentComplete = 12,
                ActiveBackend = ScanBackend.NtfsMft,
                MftBytesScanned = 120_000_000,
                MftTotalBytes = 1_000_000_000,
                EstimatedRemainingTime = TimeSpan.FromSeconds(30),
            });
            Assert.True(release.Wait(WorkerDeadline), "the test never released the scan");
            token.ThrowIfCancellationRequested();
            publisher.Publish(root);
            return Task.FromResult(new ScanResult
            {
                RootPath = rootPath,
                Root = root,
                Backend = ScanBackend.NtfsMft,
                IsComplete = true,
                Duration = TimeSpan.FromMilliseconds(10),
            });
        });

        shell.StartScan(@"C:\");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

        StaRunner.PumpUntil(() => shell.ProgressBarControl.Value == 12
            && shell.ProgressText.Contains("Indexing folders", StringComparison.Ordinal));
        Assert.Equal("12%", shell.PercentText);
        Assert.Contains("MFT", shell.ProgressText, StringComparison.Ordinal);
        Assert.Contains("about 30s left in this pass", shell.ProgressText, StringComparison.Ordinal);

        release.Set();
        StaRunner.PumpUntil(() => !shell.IsScanning);
        Assert.Equal(100, shell.ProgressBarControl.Value);
        Assert.Equal("100%", shell.PercentText);
        Assert.Equal(string.Empty, shell.ProgressText);
    });

    private static ScanShell CreateShell(ScanRunner? scanRunner = null)
    {
        ScanOptions options = ScanOptions.Default with
        {
            Backend = ScanBackendPreference.Portable,
            ProgressInterval = TimeSpan.Zero,
        };
        ScanShell shell = new(scanRunner, options);
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
