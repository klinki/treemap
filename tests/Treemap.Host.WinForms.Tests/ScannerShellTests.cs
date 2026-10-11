using Treemap;
using Treemap.Scanning;
using Xunit;

namespace Treemap.Host.WinForms.Tests;

/// <summary>
/// Covers the scan shell's contract with the scanner: a real folder reaches the frame and
/// a finished status, the completion line reports the backend and what was left out, the
/// window still fits its controls at the smallest size it allows, and the controls, the
/// current treemap and the callbacks behave while a scan is cancelled, failed, superseded
/// or disposed.
/// </summary>
/// <remarks>
/// The real scanner is exercised once, against a tiny temporary directory with the
/// portable backend forced, so no test touches a volume or the native MFT reader. The
/// lifecycle cases substitute a stand-in, which makes them deterministic: the scan ends
/// exactly when the test says it does.
/// </remarks>
public class ScannerShellTests
{
    /// <summary>Bound on any wait that depends on another thread making progress.</summary>
    private static readonly TimeSpan WorkerDeadline = TimeSpan.FromSeconds(10);

    private static readonly ScanOptions PortableOnly = ScanOptions.Default with
    {
        Backend = ScanBackendPreference.Portable,
        ProgressInterval = TimeSpan.Zero,
    };

    [Fact]
    public void AScanOfARealFolderReachesTheFrameAndTheFinishedStatus() => StaRunner.Run(() =>
    {
        using TempFolder folder = new();
        folder.Write("alpha.txt", 4096);
        folder.Write(Path.Combine("nested", "beta.txt"), 8192);

        using ScanShell shell = CreateShell(scanOptions: PortableOnly);
        shell.StartScan(folder.Root);

        StaRunner.PumpUntil(() => !shell.IsScanning && shell.StatusText.StartsWith("Finished", StringComparison.Ordinal));

        TreemapSnapshot snapshot = shell.Publisher.Current;

        // The scanner publishes the finished tree once, from its own thread. The view is
        // told about it by that publication, not by anything here.
        Assert.Equal(1L, snapshot.Version);
        Assert.Equal(Path.GetFileName(folder.Root), snapshot.Root.Name);
        Assert.Equal(4, snapshot.NodeCount);

        TreemapNode alpha = Assert.Single(snapshot.Root.Children, child => child.Name == "alpha.txt");
        StaRunner.PumpUntil(() => shell.View.Controller.CurrentFrame.Any(cell => cell.NodeId == alpha.Id));

        Assert.Contains("portable", shell.BackendText, StringComparison.Ordinal);
        Assert.DoesNotContain("fell back", shell.BackendText, StringComparison.OrdinalIgnoreCase);
        Assert.True(shell.CanScan);
        Assert.False(shell.CanCancel);
    });

    [Fact]
    public void TheStatusLineReportsTheBackendFallbackAndWhatIsMissing() => StaRunner.Run(() =>
    {
        const string Reason = "The volume could not be opened for reading.";
        const string Budget = "Stopped after 500000 entries, the configured budget.";

        ScanResult result = new()
        {
            RootPath = @"C:\",
            Root = Trees.Folder(1, "C:"),
            Backend = ScanBackend.Portable,
            FallbackFrom = ScanBackend.NtfsMft,
            FallbackReason = Reason,
            IsComplete = false,
            IncompleteReason = Budget,
            FileCount = 1234,
            DirectoryCount = 56,
            TotalLogicalSize = 12_345_678_901L,
            Duration = TimeSpan.FromSeconds(2.5),
            Diagnostics =
            [
                new ScanDiagnostic(ScanDiagnosticSeverity.Error, @"C:\Windows\System32\config", "Access denied."),
                new ScanDiagnostic(ScanDiagnosticSeverity.Warning, @"C:\pagefile.sys", "Not followed."),
            ],
            SuppressedDiagnostics = 3,
        };

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            publisher.Publish(result.Root);
            return Task.FromResult(result);
        });

        shell.StartScan(@"C:\");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Contains("portable", shell.BackendText, StringComparison.Ordinal);
        Assert.Contains("NTFS MFT", shell.BackendText, StringComparison.Ordinal);
        Assert.Contains(Reason, shell.StatusText, StringComparison.Ordinal);
        Assert.Contains("Incomplete: " + Budget, shell.StatusText, StringComparison.Ordinal);

        // Only the error counts; a warning is a deliberate omission, not a failure.
        Assert.Contains("1 entry could not be read.", shell.StatusText, StringComparison.Ordinal);
        Assert.Contains("3 further problems not listed.", shell.StatusText, StringComparison.Ordinal);

        // Counts, not paths: a single line of status has no room for a diagnostic dump.
        Assert.DoesNotContain(@"C:\Windows", shell.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Access denied", shell.StatusText, StringComparison.Ordinal);
    });

    [Fact]
    public void CancellingARunningScanKeepsTheCurrentTreemapAndFreesTheControls() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();

        using ScanShell shell = CreateShell(scanRunner: BlockingScan(entered));

        // A previous, good scan is what the user is looking at when the new one is abandoned.
        shell.Publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => HasCell(shell, 42));
        Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);

        shell.StartScan(@"C:\Windows");

        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");
        StaRunner.PumpUntil(() => shell.ProgressText.Contains("12", StringComparison.Ordinal));

        Assert.True(shell.IsScanning);
        Assert.False(shell.CanScan);
        Assert.True(shell.CanCancel);

        shell.CancelScan();
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Contains("cancelled", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.True(shell.CanScan);
        Assert.False(shell.CanCancel);

        // Nothing was published, so the previous tree is still the one on screen.
        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.True(HasCell(shell, 42));
    });

    [Fact]
    public void AFailedScanReportsTheFailureAndKeepsTheCurrentTreemap() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(scanRunner: FailingScan(
            new DirectoryNotFoundException("Could not find a part of the path.")));

        shell.Publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => HasCell(shell, 42));
        Assert.IsType<WindowsFormsSynchronizationContext>(SynchronizationContext.Current);

        shell.StartScan(@"C:\not-here");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.StartsWith("Scan failed:", shell.StatusText, StringComparison.Ordinal);
        Assert.Contains("Could not find a part of the path.", shell.StatusText, StringComparison.Ordinal);
        Assert.True(shell.CanScan);
        Assert.False(shell.CanCancel);
        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.True(HasCell(shell, 42));
    });

    [Fact]
    public void AnOutOfMemoryScanReportsMemoryFailureAndFreesTheControls() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell(scanRunner: FailingScan(new OutOfMemoryException()));

        shell.Publisher.Publish(Trees.WithMarker(42));
        StaRunner.PumpUntil(() => HasCell(shell, 42));

        shell.StartScan(@"C:\big");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        // Observed as a visible failure, never as success and never stale.
        Assert.StartsWith("Scan failed:", shell.StatusText, StringComparison.Ordinal);
        Assert.Contains("out of memory", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Scanning", shell.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("choosing", shell.BackendText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, shell.ProgressText);
        Assert.Equal(0, shell.ProgressBarControl.Value);
        Assert.False(shell.IsScanning);
        Assert.True(shell.CanScan);
        Assert.False(shell.CanCancel);

        // Nothing valid was kept: the prior map is still the one on screen.
        Assert.Equal(1L, shell.Publisher.Current.Version);
        Assert.True(HasCell(shell, 42));
    });

    [Fact]
    public void AProgressTickFromASupersededScanIsIgnored() => StaRunner.Run(() =>
    {
        int runs = 0;
        IProgress<ScanProgress>? fromFirstScan = null;

        ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            bool first = Interlocked.Increment(ref runs) == 1;
            if (first)
            {
                fromFirstScan = progress;
            }

            publisher.Publish(Trees.WithMarker(first ? 42 : 43));
            progress.Report(new ScanProgress(
                ScanPhase.Enumerating,
                rootPath,
                EntriesScanned: first ? 11 : 22,
                BytesScanned: first ? 1111 : 2222));

            return Task.FromResult(Result(rootPath, first ? 42 : 43));
        });

        shell.StartScan(@"C:\first");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        shell.StartScan(@"C:\second");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        // A finished scan clears the progress line, and nothing may put text back on it.
        Assert.Equal(string.Empty, shell.ProgressText);

        // A tick produced by the first scan and delivered after both scans are done.
        fromFirstScan!.Report(new ScanProgress(ScanPhase.Enumerating, @"C:\first", 9999, 9999));
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(string.Empty, shell.ProgressText);
        Assert.False(shell.IsScanning);
    });

    [Fact]
    public void AShellWithNoStartupRootScansNothingOnItsOwn() => StaRunner.Run(() =>
    {
        using ScanShell shell = CreateShell();

        shell.StartAfterShown();
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(300));

        Assert.False(shell.IsScanning);
        Assert.Equal(0L, shell.Publisher.Current.Version);
        Assert.DoesNotContain("Scanning", shell.StatusText, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public void AStartupRootIsScannedOnlyOnceTheWindowHasBeenShown() => StaRunner.Run(() =>
    {
        using TempFolder folder = new();
        folder.Write("alpha.txt", 2048);

        using ScanShell shell = CreateShell(scanOptions: PortableOnly, startupPath: folder.Root);

        // Nothing runs before the window is shown: the view has no handle yet.
        Assert.False(shell.IsScanning);
        Assert.Equal(0L, shell.Publisher.Current.Version);

        shell.StartAfterShown();
        StaRunner.PumpUntil(() => !shell.IsScanning && shell.StatusText.StartsWith("Finished", StringComparison.Ordinal));
        Assert.Equal(1L, shell.Publisher.Current.Version);

        // Shown again, the argument is spent, so the same root is not rescanned.
        shell.StartAfterShown();
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.False(shell.IsScanning);
        Assert.Equal(1L, shell.Publisher.Current.Version);
    });

    /// <summary>
    /// The window at the size it refuses to go below, carrying the longest status a scan
    /// can produce: every control must still sit inside its parent, and the view must
    /// keep a usable area.
    /// </summary>
    [Fact]
    public void AtTheSmallestWindowTheToolbarWrapsAndTheStatusAndViewStillFit() => StaRunner.Run(() =>
    {
        const string Reason =
            "The NTFS master file table could not be opened for reading without an elevated " +
            "handle, so the portable directory walk was used instead.";

        const string Budget = "Stopped after the configured entry budget, so part of the tree was never walked.";

        ScanResult result = new()
        {
            RootPath = @"C:\",
            Root = Trees.Folder(1, @"C:\"),
            Backend = ScanBackend.Portable,
            FallbackFrom = ScanBackend.NtfsMft,
            FallbackReason = Reason,
            IsComplete = false,
            IncompleteReason = Budget,
            FileCount = 1_234_567,
            DirectoryCount = 45_678,
            TotalLogicalSize = 512_000_000_000L,
            Duration = TimeSpan.FromSeconds(41.2),
            Diagnostics = [new ScanDiagnostic(ScanDiagnosticSeverity.Error, @"C:\System Volume Information", "Access denied.")],
            SuppressedDiagnostics = 17,
        };

        using ScanShell shell = CreateShell(scanRunner: (rootPath, publisher, options, progress, token) =>
        {
            publisher.Publish(result.Root);
            return Task.FromResult(result);
        });

        shell.StartScan(@"C:\");
        StaRunner.PumpUntil(() => !shell.IsScanning);

        Assert.Contains(Reason, shell.StatusText, StringComparison.Ordinal);

        // MinimumSize is the outer window, so this is the smallest client area the user
        // can actually reach - and the width at which the toolbar stops fitting on one row.
        shell.Size = shell.MinimumSize;

        // Filling the client width is what proves the layout has been re-run at the new
        // size, rather than the view still reporting the one it was given at startup.
        StaRunner.PumpUntil(() =>
        {
            shell.PerformLayout();
            return shell.View.Width == shell.ClientSize.Width;
        });

        Rectangle client = new(Point.Empty, shell.ClientSize);

        Control[] controls = Descendants(shell).ToArray();
        string[] buttons = controls.OfType<Button>().Select(button => button.Text).ToArray();

        // Only the three scan status lines: the filter row carries its own labels.
        Label[] statusLines = controls.OfType<Label>()
            .Where(label => label.Parent is not null && label.Parent.Dock == DockStyle.Bottom)
            .ToArray();

        // Guards the loops below from passing on an empty enumeration.
        Assert.Contains("Scan", buttons);
        Assert.Contains("Cancel", buttons);
        Assert.Equal(3, statusLines.Length);

        // Every control inside its parent: the toolbar wraps instead of clipping, so the
        // controls that have to stay reachable are still on screen at the bottom row.
        foreach (Control control in controls)
        {
            Rectangle parent = new(Point.Empty, control.Parent!.ClientSize);

            Assert.True(
                parent.Contains(control.Bounds),
                $"{Describe(control)} at {control.Bounds} escaped its parent's client area {parent}.");
        }

        // The status lines are capped at the client width so the fallback reason wraps
        // rather than running off the edge.
        foreach (Label label in statusLines)
        {
            Assert.True(label.MaximumSize.Width > 0, $"{Describe(label)} has no usable width cap");
            Assert.True(
                label.MaximumSize.Width <= client.Width && label.Width <= client.Width,
                $"{Describe(label)} is {label.Width} wide with a {label.MaximumSize.Width} cap inside {client.Width}");
        }

        Assert.Contains("Incomplete: " + Budget, shell.StatusText, StringComparison.Ordinal);

        // The panels must leave the view a usable area rather than squeezing it out.
        Assert.True(shell.View.Height > 0 && shell.View.Width > 0, $"the view has no room at {shell.View.Bounds}");
        Assert.True(client.Contains(shell.View.Bounds), $"the view at {shell.View.Bounds} escaped {client}");

        SaveForInspection(shell, "scan-shell.png");
    });

    [Fact]
    public void DisposingTheShellCancelsTheScanWithoutWaitingForIt() => StaRunner.Run(() =>
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim cancelled = new();

        IProgress<ScanProgress>? lateProgress = null;

        ScanRunner runner = async (rootPath, publisher, options, progress, token) =>
        {
            lateProgress = progress;
            entered.Set();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.Set();
                throw;
            }

            throw new InvalidOperationException("the injected scan was never cancelled");
        };

        ScanShell shell = CreateShell(scanRunner: runner);
        shell.StartScan(@"C:\Windows");
        Assert.True(entered.Wait(WorkerDeadline), "the injected scan never started");

        // Dispose returns while the scan is still running; waiting here would hang instead.
        shell.Dispose();
        // Native labels can clear their text while their handles are destroyed. Late
        // callbacks must preserve the state that disposal actually left behind.
        string statusAfterClose = shell.StatusText;

        Assert.True(cancelled.Wait(WorkerDeadline), "disposing the shell did not cancel the scan");

        // The completion callback runs with the window gone, and must find nothing to update.
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));
        Assert.Equal(statusAfterClose, shell.StatusText);

        // A progress tick arriving after teardown is dropped the same way.
        lateProgress!.Report(new ScanProgress(ScanPhase.Building, @"C:\Windows", 4321, 4321));
        StaRunner.PumpFor(TimeSpan.FromMilliseconds(200));

        Assert.Equal(statusAfterClose, shell.StatusText);

        // Left exactly as teardown found them: the post-dispose completion path must not
        // have re-enabled anything.
        Assert.False(shell.CanScan);
        Assert.True(shell.CanCancel);
    });

    // ---- helpers ----

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

        // Showing the form creates the rest of this for us, and both halves are load-bearing:
        // the layout is what gives the view its real bounds and viewport, and the handle is
        // what lets it consume a publication at all. TreemapForm keeps a published snapshot
        // pending until its own handle exists - OnHandleCreated is what reposts it - so a
        // hidden shell without this step never redraws, and every frame assertion below
        // times out on a shell that scanned perfectly.
        shell.PerformLayout();
        _ = shell.View.Handle;

        // Every descendant handle on this STA thread, so a later worker touching a
        // control trips the cross-thread check instead of hanging at teardown.
        foreach (Control descendant in Descendants(shell))
        {
            _ = descendant.Handle;
        }

        return shell;
    }

    /// <summary>
    /// A scan that runs until it is cancelled, reporting one progress tick on the way in.
    /// It never publishes, so nothing on screen changes while it is running.
    /// </summary>
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

    private static ScanRunner FailingScan(Exception failure)
    {
        async Task<ScanResult> Run(
            string rootPath,
            SnapshotPublisher publisher,
            ScanOptions options,
            IProgress<ScanProgress> progress,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw failure;
        }

        return Run;
    }

    private static ScanResult Result(string rootPath, int markerId) => new()
    {
        RootPath = rootPath,
        Root = Trees.WithMarker(markerId),
        Backend = ScanBackend.Portable,
        IsComplete = true,
        FileCount = 1,
        Duration = TimeSpan.FromMilliseconds(10),
    };

    private static bool HasCell(ScanShell shell, int nodeId)
        => shell.View.Controller.CurrentFrame.Any(cell => cell.NodeId == nodeId);

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

    /// <summary>Names a control in an assertion message.</summary>
    private static string Describe(Control control)
        => $"'{control.Text}' ({control.GetType().Name})";

    /// <summary>
    /// Writes the window to the git-ignored artifacts folder for visual inspection.
    /// Best effort: the assertions stand on their own, so not finding the repository root
    /// is not a failure. Rendering faults propagate; only the artifact file IO is best-effort.
    /// </summary>
    private static void SaveForInspection(ScanShell shell, string fileName)
    {
        string? root = RepositoryRoot();

        if (root is null)
        {
            return;
        }

        string directory = Path.Combine(root, "artifacts", "verification", "scanner");

        try
        {
            Directory.CreateDirectory(directory);

            // The shell stays hidden. Top-level DrawToBitmap skips invisible children,
            // so every descendant paints at its shell-relative offset instead.
            shell.PerformLayout();

            using Bitmap bitmap = new(Math.Max(1, shell.ClientSize.Width), Math.Max(1, shell.ClientSize.Height));
            using (Graphics canvas = Graphics.FromImage(bitmap))
            {
                canvas.Clear(shell.BackColor);

                foreach (Control control in Descendants(shell))
                {
                    if (control.Width <= 0 || control.Height <= 0)
                    {
                        continue;
                    }

                    Point offset = Point.Empty;
                    for (Control? ancestor = control; ancestor is not null && ancestor != shell; ancestor = ancestor.Parent)
                    {
                        offset.Offset(ancestor.Location);
                    }

                    using Bitmap child = new(control.Width, control.Height);
                    control.DrawToBitmap(child, new Rectangle(Point.Empty, control.Size));
                    canvas.DrawImage(child, offset);
                }
            }

            bitmap.Save(Path.Combine(directory, fileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The artifact is a convenience; the layout assertions are the test.
        }
    }

    private static string? RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Treemap.slnx")))
            {
                return directory.FullName;
            }
        }

        return null;
    }

    /// <summary>A throwaway directory that deletes itself.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Root { get; }

        public TempFolder()
        {
            Root = Path.Combine(Path.GetTempPath(), "treemap-shell-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Write(string relativePath, int bytes)
        {
            string full = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[bytes]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // A file left open by the scanner would fail the delete; the temp folder
                // is small and the OS will collect it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
