using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Live backend status in the shell: the backend label moves off "choosing" as soon as
/// a backend attempt starts, and a mid-scan fallback shows its reason at the transition
/// rather than only in the finished status line.
/// </summary>
public class LiveBackendStatusTests
{
    /// <summary>Bound on any wait that depends on another thread making progress.</summary>
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    [Fact]
    public void BackendLabelTracksSelectionAndFallbackWhileScanning() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim proceed = new();
        using ManualResetEventSlim enteredFallback = new();
        using ManualResetEventSlim proceedFallback = new();

        ScanRunner runner = (rootPath, publisher, options, progress, token) =>
        {
            try
            {
                progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 10, 100)
                {
                    ActiveBackend = ScanBackend.NtfsMft,
                });
                entered.Set();

                proceed.Wait(WorkerDeadline, token);
                token.ThrowIfCancellationRequested();

                progress.Report(new ScanProgress(ScanPhase.Enumerating, rootPath, 5, 50)
                {
                    ActiveBackend = ScanBackend.Portable,
                    FallbackFrom = ScanBackend.NtfsMft,
                    FallbackReason = "MFT unreadable",
                });
                enteredFallback.Set();

                proceedFallback.Wait(WorkerDeadline, token);
                token.ThrowIfCancellationRequested();

                TreemapNode root = Trees.Folder(1, "root");
                publisher.Publish(root, rootPath);
                return Task.FromResult(new ScanResult
                {
                    RootPath = rootPath,
                    Root = root,
                    Backend = ScanBackend.Portable,
                    FallbackFrom = ScanBackend.NtfsMft,
                    FallbackReason = "MFT unreadable",
                    IsComplete = true,
                    Duration = TimeSpan.FromMilliseconds(10),
                });
            }
            catch
            {
                entered.Set();
                enteredFallback.Set();
                throw;
            }
        };

        using ScanShell shell = CreateShell(scanRunner: runner);
        try
        {
            shell.StartScan(@"C:\live-backend");

            Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

            // The selection is visible while the scan is still running, not after it.
            StaRunner.PumpUntil(() => shell.BackendText.Contains("NTFS", StringComparison.Ordinal));
            Assert.True(shell.IsScanning);

            proceed.Set();

            // The fallback is visible while the retry is still blocked, before any
            // final publish: this is the live transition, not the finished result.
            Assert.True(enteredFallback.Wait(WorkerDeadline), "the fallback never started");
            StaRunner.PumpUntil(() => shell.StatusText.Contains("MFT unreadable", StringComparison.Ordinal));

            Assert.True(shell.IsScanning, "the scan finished before the live fallback was asserted");
            Assert.Contains("portable", shell.BackendText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("fell back", shell.BackendText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            proceed.Set();
            proceedFallback.Set();
        }

        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Contains("portable", shell.BackendText, StringComparison.Ordinal);
        Assert.Contains("fell back", shell.BackendText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MFT unreadable", shell.StatusText, StringComparison.Ordinal);
    });

    /// <summary>
    /// A shell with the handles a shown window has, but no window: handles are what posted
    /// callbacks need as a target, and showing one would flash a window during the run.
    /// </summary>
    private static ScanShell CreateShell(
        ScanRunner? scanRunner = null,
        ScanOptions? scanOptions = null,
        string? startupPath = null)
    {
        ScanShell shell = new(scanRunner, scanOptions, startupPath);
        _ = shell.Handle;

        shell.PerformLayout();
        _ = shell.View.Handle;

        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        return shell;
    }

    /// <summary>Every control below <paramref name="parent"/>, at any depth.</summary>
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
