using System.Globalization;
using Treemap;
using Treemap.Scanning;
using Treemap.Scanning.Windows;

namespace Treemap.Host.WinForms;

/// <summary>
/// One scan as the shell asks for it. Production uses
/// <see cref="FileSystemScanner.ScanAsync"/>; tests substitute a stand-in.
/// </summary>
internal delegate Task<ScanResult> ScanRunner(
    string rootPath,
    SnapshotPublisher publisher,
    ScanOptions options,
    IProgress<ScanProgress> progress,
    CancellationToken cancellationToken);

/// <summary>
/// The window: a root to scan, the buttons that start and stop it, three lines of status,
/// and the treemap view the finished tree is published into.
/// </summary>
/// <remarks>
/// Scanning runs on a worker thread and every callback returns through the UI
/// synchronization context. Each start takes the next generation number and hands it to
/// that scan's callbacks, which drop anything from an older or finished one. Nothing is
/// published here: the scanner publishes intermediate previews as they arrive and a final
/// snapshot at the end, restoring the prior snapshot when a scan is cancelled or fails.
/// </remarks>
internal sealed class ScanShell : Form
{
    /// <summary>The id the scanner gives its own root, and so the placeholder's.</summary>
    private const int RootId = 1;

    private const long Mb = 1_000_000L;
    private const long Gb = 1_000L * Mb;

    /// <summary>Label width kept free on each side when wrapping the status lines.</summary>
    private const int StatusMargin = 16;

    private static TreemapNode PlaceholderRoot()
        => TreemapNode.Create(RootId, "Nothing scanned yet", NodeKind.Folder, 0, 0, null, 0);

    private readonly SnapshotPublisher _publisher = new(PlaceholderRoot());
    private readonly TreemapForm _view;
    private readonly TextBox _pathBox = new();
    private readonly Button _browseButton = new() { Text = "Browse...", AutoSize = true };
    private readonly ComboBox _volumeBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly Button _refreshButton = new() { Text = "Volumes", AutoSize = true };
    private readonly Button _scanButton = new() { Text = "Scan", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Label _backendLabel = new() { AutoSize = true };
    private readonly Label _progressLabel = new() { AutoSize = true };
    private readonly Label _statusLabel = new() { AutoSize = true };

    private readonly Button _backButton = new() { Text = "Back", AutoSize = true, Enabled = false };
    private readonly Button _forwardButton = new() { Text = "Forward", AutoSize = true, Enabled = false };
    private readonly Button _upButton = new() { Text = "Up", AutoSize = true, Enabled = false };
    private readonly Button _homeButton = new() { Text = "Home", AutoSize = true, Enabled = false };
    private readonly Button _moreButton = new() { Text = "More detail", AutoSize = true };
    private readonly Button _lessButton = new() { Text = "Less detail", AutoSize = true, Enabled = false };
    private readonly CheckBox _freeBox = new() { Text = "Free space", Appearance = Appearance.Button, AutoSize = true };
    private readonly CheckBox _unaccountedBox = new() { Text = "Unaccounted space", Appearance = Appearance.Button, AutoSize = true };

    private bool _refreshingNav;

    /// <summary>One scanner for the shell, so node ids stay put across rescans.</summary>
    private readonly FileSystemScanner _scanner = new();

    private readonly ScanRunner _runner;
    private readonly ScanOptions _options;

    private readonly bool _useProductionScanner;
    private readonly bool _hasCustomProbe;
    private readonly ScanAccessProbe _accessProbe;
    private readonly ElevationCheck _isElevated;
    private readonly ElevationStarter _launcher;
    private readonly TrustedExeResolver _trustedExe;
    private readonly bool _isElevatedRetry;
    private readonly bool _elevationDisabled;

    private string? _startupPath;
    private CancellationTokenSource? _scanCts;
    private int _generation;
    private bool _disposed;

    internal ScanShell(
        ScanRunner? scanRunner = null,
        ScanOptions? scanOptions = null,
        string? startupPath = null,
        bool isElevatedRetry = false,
        bool disableElevation = false,
        ScanAccessProbe? accessProbe = null,
        ElevationCheck? elevationCheck = null,
        ElevationStarter? elevationLauncher = null,
        TrustedExeResolver? trustedExeResolver = null)
    {
        Text = "Treemap";
        ClientSize = new Size(1100, 680);
        MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = SystemColors.Control;

        _runner = scanRunner ?? RunScanner;
        _useProductionScanner = scanRunner is null;
        _hasCustomProbe = accessProbe is not null;
        _accessProbe = accessProbe ?? DefaultAccessProbe;
        _isElevated = elevationCheck ?? Elevation.IsProcessElevated;
        _launcher = elevationLauncher ?? ((exe, root, flag) => Elevation.StartElevated(exe, root, flag));
        _trustedExe = trustedExeResolver ?? Elevation.ResolveTrustedExePath;
        _isElevatedRetry = isElevatedRetry;
        _elevationDisabled = disableElevation;
        _options = scanOptions ?? ScanOptions.Default with
        {
            // The scanner throttles against this itself; a quarter second is often enough
            // for a label and cheap enough for a volume walk.
            ProgressInterval = TimeSpan.FromMilliseconds(250),

            // The desktop shows the scan as it arrives. Explicitly injected options
            // are left exactly as given so tests stay predictable.
            PublishIntermediateSnapshots = true,
        };

        _startupPath = startupPath;
        _pathBox.Text = startupPath ?? string.Empty;

        _view = new TreemapForm(_publisher, ResolveName) { Dock = DockStyle.Fill };

        BuildLayout();

        _scanButton.Click += (_, _) => StartScan(_pathBox.Text);
        _cancelButton.Click += (_, _) => CancelScan();
        _browseButton.Click += OnBrowseClick;
        _refreshButton.Click += (_, _) => _ = LoadVolumesAsync();
        _volumeBox.SelectedIndexChanged += OnVolumeSelected;
        _pathBox.KeyDown += OnPathKeyDown;

        _backButton.Click += (_, _) => Navigate(() => _view.Controller.GoBack());
        _forwardButton.Click += (_, _) => Navigate(() => _view.Controller.GoForward());
        _upButton.Click += (_, _) => Navigate(() => _view.Controller.GoToParent());
        _homeButton.Click += (_, _) => Navigate(() => _view.Controller.GoToRoot());
        _moreButton.Click += (_, _) => ChangeDetail(increase: true);
        _lessButton.Click += (_, _) => ChangeDetail(increase: false);
        _freeBox.CheckedChanged += (_, _) => ApplySpaceVisibility();
        _unaccountedBox.CheckedChanged += (_, _) => ApplySpaceVisibility();

        _view.Controller.Changed += OnViewChanged;
        RefreshNav();
    }

    // ---- state the tests read ----

    internal SnapshotPublisher Publisher => _publisher;

    internal TreemapForm View => _view;

    internal bool IsScanning => _scanCts is not null;

    internal bool CanScan => _scanButton.Enabled;

    internal bool CanCancel => _cancelButton.Enabled;

    internal string BackendText => _backendLabel.Text;

    internal string ProgressText => _progressLabel.Text;

    internal string StatusText => _statusLabel.Text;

    internal Button BackButton => _backButton;

    internal Button ForwardButton => _forwardButton;

    internal Button UpButton => _upButton;

    internal Button HomeButton => _homeButton;

    internal Button MoreDetailButton => _moreButton;

    internal Button LessDetailButton => _lessButton;

    internal CheckBox FreeSpaceBox => _freeBox;

    internal CheckBox UnaccountedSpaceBox => _unaccountedBox;

    // ---- layout ----

    private void BuildLayout()
    {
        _pathBox.Width = 320;
        _pathBox.Margin = new Padding(0, 3, 8, 0);
        _pathBox.PlaceholderText = "Folder or volume to scan";

        // Wrapping is what keeps Scan and Cancel reachable when the window is narrow:
        // otherwise they are the controls clipped away first.
        FlowLayoutPanel toolbar = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6, 4, 6, 4),
        };

        toolbar.Controls.Add(_pathBox);
        toolbar.Controls.Add(_browseButton);
        toolbar.Controls.Add(_volumeBox);
        toolbar.Controls.Add(_refreshButton);
        toolbar.Controls.Add(_scanButton);
        toolbar.Controls.Add(_cancelButton);

        // Reference-style navigation and view controls: history, actual parent,
        // visible depth and the two independent space toggles. Compact buttons in
        // a wrapping row so they stay reachable when the window is narrow.
        FlowLayoutPanel navBar = new()
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6, 0, 6, 4),
        };

        navBar.Controls.Add(_backButton);
        navBar.Controls.Add(_forwardButton);
        navBar.Controls.Add(_upButton);
        navBar.Controls.Add(_homeButton);
        navBar.Controls.Add(_moreButton);
        navBar.Controls.Add(_lessButton);
        navBar.Controls.Add(_freeBox);
        navBar.Controls.Add(_unaccountedBox);

        FlowLayoutPanel status = new()
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Padding = new Padding(6, 2, 6, 4),
        };

        status.Controls.Add(_backendLabel);
        status.Controls.Add(_progressLabel);
        status.Controls.Add(_statusLabel);

        // Added fill first: WinForms docks from the last child back, so the view is laid
        // out last and takes whatever the two panels leave behind. The navigation bar
        // is added before the scan toolbar so it docks directly beneath it.
        Controls.Add(_view);
        Controls.Add(navBar);
        Controls.Add(toolbar);
        Controls.Add(status);

        FitStatusWidth();
    }

    /// <summary>
    /// Caps the status labels at the client width so a long fallback reason wraps.
    /// Skipped once teardown has started, so a client-size shift while children are
    /// being torn down cannot re-enter layout on half-disposed controls.
    /// </summary>
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);

        if (_disposed)
        {
            return;
        }

        FitStatusWidth();
    }

    private void FitStatusWidth()
    {
        if (_disposed)
        {
            return;
        }

        int width = Math.Max(120, ClientSize.Width - StatusMargin);

        _backendLabel.MaximumSize = new Size(width, 0);
        _progressLabel.MaximumSize = new Size(width, 0);
        _statusLabel.MaximumSize = new Size(width, 0);
    }

    // ---- startup ----

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Split out so the startup path is reachable without a window.
        StartAfterShown();
    }

    /// <summary>
    /// The work that needs a real window: the volume list, and the scan an argument asked
    /// for. Starting here rather than in the constructor is what lets the first snapshot
    /// be posted to a view that exists.
    /// </summary>
    internal void StartAfterShown()
    {
        _ = LoadVolumesAsync();

        if (_startupPath is { Length: > 0 } path)
        {
            // Consumed, so a shown window that is shown again does not rescan it.
            _startupPath = null;
            StartScan(path);
        }
    }

    private async Task LoadVolumesAsync()
    {
        IReadOnlyList<ScannableVolume> volumes;

        try
        {
            volumes = await FileSystemScanner.GetReadyVolumesAsync();
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or ArgumentException
            or NotSupportedException)
        {
            if (!_disposed)
            {
                _statusLabel.Text = "Ready volumes could not be listed; enter a path instead.";
            }

            return;
        }

        if (_disposed)
        {
            return;
        }

        _volumeBox.BeginUpdate();
        try
        {
            _volumeBox.Items.Clear();
            _volumeBox.Items.Add(VolumeChoice.Prompt);

            foreach (ScannableVolume volume in volumes)
            {
                _volumeBox.Items.Add(new VolumeChoice(volume));
            }

            _volumeBox.SelectedIndex = 0;
        }
        finally
        {
            _volumeBox.EndUpdate();
        }
    }

    private void OnVolumeSelected(object? sender, EventArgs e)
    {
        if (!IsScanning && _volumeBox.SelectedItem is VolumeChoice { Volume: { } volume })
        {
            _pathBox.Text = volume.RootDirectory;
        }
    }

    private void OnBrowseClick(object? sender, EventArgs e)
    {
        using FolderBrowserDialog dialog = new() { UseDescriptionForTitle = true, Description = "Folder to scan" };

        if (Directory.Exists(_pathBox.Text))
        {
            dialog.SelectedPath = _pathBox.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dialog.SelectedPath;
        }
    }

    private void OnPathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter)
        {
            return;
        }

        e.SuppressKeyPress = true;
        StartScan(_pathBox.Text);
    }

    // ---- navigation and view ----

    /// <summary>
    /// Runs one history navigation and re-lays out the newly focused subtree.
    /// Layout only: navigation never rescans.
    /// </summary>
    private void Navigate(Func<bool> move)
    {
        if (_disposed)
        {
            return;
        }

        if (move())
        {
            _view.Controller.Relayout(animate: true);
        }

        RefreshNav();
    }

    private void ChangeDetail(bool increase)
    {
        if (_disposed)
        {
            return;
        }

        bool changed = increase
            ? _view.Controller.IncreaseDetail()
            : _view.Controller.DecreaseDetail();

        if (changed)
        {
            _view.Controller.Relayout(animate: true);
        }

        RefreshNav();
    }

    /// <summary>
    /// Layout-only space filters. Hiding a block reflows the map; the snapshot,
    /// scan counts and totals are preserved and nothing rescans.
    /// </summary>
    private void ApplySpaceVisibility()
    {
        if (_disposed || _refreshingNav)
        {
            return;
        }

        _view.Controller.SetSpaceVisibility(_freeBox.Checked, _unaccountedBox.Checked);
        _view.Controller.Relayout(animate: true);
        RefreshNav();
    }

    private void OnViewChanged(ChangeKind kind)
    {
        if (_disposed)
        {
            return;
        }

        RefreshNav();
    }

    /// <summary>
    /// Mirrors controller state onto the toolbar: history buttons enable with
    /// entries, detail buttons disable at their depth limits, and the space
    /// boxes check with the layout filters.
    /// </summary>
    internal void RefreshNav()
    {
        if (_disposed)
        {
            return;
        }

        TreemapController controller = _view.Controller;
        _refreshingNav = true;
        try
        {
            _backButton.Enabled = controller.CanGoBack;
            _forwardButton.Enabled = controller.CanGoForward;
            _upButton.Enabled = controller.CanGoUp;
            _homeButton.Enabled = controller.CanGoHome;
            _moreButton.Enabled = controller.CanIncreaseDetail;
            _lessButton.Enabled = controller.CanDecreaseDetail;
            _freeBox.Checked = controller.ShowFreeSpace;
            _unaccountedBox.Checked = controller.ShowUnknownSpace;
        }
        finally
        {
            _refreshingNav = false;
        }
    }

    // ---- scanning ----

    private Task<ScanResult> RunScanner(
        string rootPath,
        SnapshotPublisher publisher,
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
        => _scanner.ScanAsync(rootPath, publisher, options, progress, cancellationToken);

    private static NtfsAccessResult DefaultAccessProbe(
        string rootPath,
        ScanOptions options,
        CancellationToken cancellationToken)
        => NtfsAccessProbe.Check(rootPath, options, cancellationToken);

    internal void StartScan(string? rootPath)
    {
        if (_disposed || IsScanning)
        {
            return;
        }

        string path = (rootPath ?? _pathBox.Text).Trim();

        if (path.Length == 0)
        {
            _statusLabel.Text = "Choose a folder or a volume to scan first.";
            return;
        }

        _pathBox.Text = path;
        _ = RunScanAsync(path, ++_generation);
    }

    /// <summary>
    /// Runs one scan to its end. Every callback is gated on that scan still being the
    /// current one, so nothing reaches a control that has moved on or gone away.
    /// </summary>
    private async Task RunScanAsync(string rootPath, int generation)
    {
        CancellationTokenSource cts = new();
        _scanCts = cts;

        SetBusy(true);
        _backendLabel.Text = "Backend: choosing...";
        _progressLabel.Text = string.Empty;
        _statusLabel.Text = $"Scanning {rootPath}";

        try
        {
            // Constructed on the UI thread, so Progress<T> captures the WinForms
            // synchronization context and delivers each report as a posted callback.
            IProgress<ScanProgress> progress = new Progress<ScanProgress>(tick => OnScanProgress(generation, tick));

            (bool relaunched, string? elevationNote) = await TryElevateAsync(rootPath, generation, cts);
            if (relaunched)
            {
                return;
            }

            // A probe failure that arrived after a cancel or a close must not start a
            // scan on a window that is gone or no longer wants one.
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation))
            {
                throw new OperationCanceledException(cts.Token);
            }

            if (elevationNote is not null)
            {
                // Shown now, while the fallback begins, and retained after it finishes.
                _statusLabel.Text = elevationNote + $" Scanning {rootPath}";
            }

            // No scheduling token: a scan cancelled before it starts still runs and
            // throws from its own cancellation checks.
            ScanResult result = await Task.Run(
                () => _runner(rootPath, _publisher, _options, progress, cts.Token),
                CancellationToken.None);

            if (IsCurrent(generation))
            {
                ShowResult(result);

                if (elevationNote is not null)
                {
                    _statusLabel.Text += " " + elevationNote;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(generation))
            {
                _backendLabel.Text = "Backend: cancelled";
                _progressLabel.Text = string.Empty;
                _statusLabel.Text = "Scan cancelled. The treemap still shows the previous scan.";
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // OutOfMemoryException is deliberately not caught: a process that cannot
            // allocate is not a scan that failed, and saying otherwise hides the reason.
            if (IsCurrent(generation))
            {
                _backendLabel.Text = "Backend: none";
                _progressLabel.Text = string.Empty;
                _statusLabel.Text = $"Scan failed: {exception.Message}";
            }
        }
        finally
        {
            // Released only now, where the work has stopped: disposing a source whose
            // token a worker may still read turns a cancel into a crash.
            cts.Dispose();

            if (ReferenceEquals(_scanCts, cts))
            {
                _scanCts = null;

                // A disposed window has no controls to re-enable.
                if (!_disposed)
                {
                    SetBusy(false);
                }
            }
        }
    }

    /// <summary>
    /// True while <paramref name="generation"/> is still the running operation. A tick from
    /// a finished scan is dropped rather than re-filling a label that was cleared.
    /// </summary>
    private bool IsCurrent(int generation) => !_disposed && _scanCts is not null && generation == _generation;

    /// <summary>
    /// Probes raw NTFS access on a worker and relaunches elevated when it is denied.
    /// Returns true only after a successful launch, in which case the caller returns
    /// without scanning; the original window closes and the new process resumes the
    /// absolute root. A note explains a declined or failed launch before the regular
    /// fallback runs.
    /// </summary>
    private async Task<(bool relaunched, string? note)> TryElevateAsync(
        string rootPath,
        int generation,
        CancellationTokenSource cts)
    {
        if (_elevationDisabled || _isElevatedRetry)
        {
            return (false, null);
        }

        if (_options.Backend == ScanBackendPreference.Portable)
        {
            return (false, null);
        }

        if (!_useProductionScanner && !_hasCustomProbe)
        {
            return (false, null);
        }

        NtfsAccessResult access;
        try
        {
            access = await Task.Run(() => _accessProbe(rootPath, _options, cts.Token), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A probe failure is an ordinary fallback, but only while the scan still
            // wants one: a cancel or close that landed first must win over the fallback.
            cts.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation) || _disposed)
            {
                throw new OperationCanceledException(cts.Token);
            }

            return (false, null);
        }

        if (!IsCurrent(generation) || cts.IsCancellationRequested)
        {
            throw new OperationCanceledException(cts.Token);
        }

        if (access.Access != NtfsRawAccess.Denied)
        {
            return (false, null);
        }

        bool elevated;
        try
        {
            elevated = _isElevated();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            elevated = false;
        }

        if (elevated)
        {
            return (false, null);
        }

        string absolute;
        try
        {
            absolute = Path.GetFullPath(rootPath);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return (false, null);
        }

        if (!IsCurrent(generation) || cts.IsCancellationRequested || _disposed)
        {
            throw new OperationCanceledException(cts.Token);
        }

        string? exe;
        try
        {
            exe = _trustedExe();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            exe = null;
        }

        if (exe is null)
        {
            return (false, "Elevation was unavailable; running without elevation.");
        }

        if (!IsCurrent(generation) || cts.IsCancellationRequested || _disposed)
        {
            throw new OperationCanceledException(cts.Token);
        }

        bool started;
        try
        {
            started = _launcher(exe, absolute, Elevation.ElevatedRetryFlag);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == Elevation.UacDeclined)
        {
            return (false, "Elevation was declined; running without elevation.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return (false, $"Elevation failed ({exception.Message}); running without elevation.");
        }

        if (!started)
        {
            return (false, "Elevation failed to start; running without elevation.");
        }

        if (!_disposed && IsCurrent(generation))
        {
            Close();
        }

        return (true, null);
    }

    private void OnScanProgress(int generation, ScanProgress tick)
    {
        if (!IsCurrent(generation))
        {
            return;
        }

        _progressLabel.Text =
            $"{DescribePhase(tick.Phase)} - {tick.EntriesScanned:N0} entries, {FormatSize(tick.BytesScanned)}";
    }

    /// <summary>
    /// Reports what the scan produced: which backend answered, why a faster one was
    /// abandoned, and how much is missing. Counts, not paths.
    /// </summary>
    private void ShowResult(ScanResult result)
    {
        _backendLabel.Text = result.FallbackFrom is { } abandoned
            ? $"Backend: {DescribeBackend(result.Backend)} (fell back from {DescribeBackend(abandoned)})"
            : $"Backend: {DescribeBackend(result.Backend)}";

        int errors = 0;
        foreach (ScanDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Severity == ScanDiagnosticSeverity.Error)
            {
                errors++;
            }
        }

        _progressLabel.Text = string.Empty;

        string status =
            $"Finished in {result.Duration.TotalSeconds:F1}s - {result.FileCount:N0} files, " +
            $"{result.DirectoryCount:N0} folders, {FormatSize(result.TotalLogicalSize)}.";

        if (!string.IsNullOrEmpty(result.FallbackReason))
        {
            status += $" Fell back: {result.FallbackReason}";
        }

        if (errors > 0)
        {
            status += $" {errors} {(errors == 1 ? "entry" : "entries")} could not be read.";
        }

        if (result.SuppressedDiagnostics > 0)
        {
            status += $" {result.SuppressedDiagnostics} further problems not listed.";
        }

        if (!result.IsComplete)
        {
            status += $" Incomplete: {result.IncompleteReason ?? "part of the tree is missing"}.";
        }

        _statusLabel.Text = status;
    }

    /// <summary>Asks the running scan to stop. It does not wait for it.</summary>
    internal void CancelScan()
    {
        if (_disposed || _scanCts is not { } cts)
        {
            return;
        }

        _statusLabel.Text = "Cancelling...";
        RequestCancel(cts);
    }

    private void SetBusy(bool busy)
    {
        // Readonly rather than disabled: the text stays legible and copyable, and
        // nothing can change what is being scanned underneath it.
        _pathBox.ReadOnly = busy;

        _browseButton.Enabled = !busy;
        _volumeBox.Enabled = !busy;
        _refreshButton.Enabled = !busy;
        _scanButton.Enabled = !busy;
        _cancelButton.Enabled = busy;
    }

    // ---- teardown ----

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing asks and carries on: a window that waits for a volume walk looks hung.
        RequestCancel(_scanCts);
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _view.Controller.Changed -= OnViewChanged;
            RequestCancel(_scanCts);
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Cancellation only requests; the scan's own continuation disposes the source.
    /// </summary>
    private static void RequestCancel(CancellationTokenSource? source)
    {
        if (source is null)
        {
            return;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scan ended between the check and the call.
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Cell label text, read out of the current snapshot, so no side table has to be
    /// kept in step with what was published.
    /// </summary>
    private string ResolveName(int id)
        => _publisher.Current.TryGetNode(id, out TreemapNode node)
            ? node.Name
            : id.ToString(CultureInfo.InvariantCulture);

    private static string DescribeBackend(ScanBackend backend)
        => backend == ScanBackend.NtfsMft ? "NTFS MFT" : "portable";

    private static string DescribePhase(ScanPhase phase) => phase switch
    {
        ScanPhase.Starting => "Starting",
        ScanPhase.Enumerating => "Reading files",
        ScanPhase.Building => "Building the tree",
        ScanPhase.Publishing => "Publishing",
        ScanPhase.Finished => "Finished",
        _ => phase.ToString(),
    };

    private static string FormatSize(long bytes)
    {
        if (bytes >= Gb)
        {
            return $"{bytes / (double)Gb:F1} GB";
        }

        if (bytes >= Mb)
        {
            return $"{bytes / (double)Mb:F0} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024.0:F0} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>One entry in the volume picker, or the prompt above the list.</summary>
    private sealed record VolumeChoice(ScannableVolume? Volume)
    {
        public static VolumeChoice Prompt { get; } = new(Volume: null);

        public override string ToString()
            => Volume is { } volume
                ? $"{volume.Name}   {volume.Format}   {volume.DriveType}   " +
                  $"{FormatSize(volume.AvailableFreeSpace)} free of {FormatSize(volume.TotalSize)}"
                : "Ready volumes...";
    }
}
