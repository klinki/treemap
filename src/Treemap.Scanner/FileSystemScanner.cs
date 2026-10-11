using System.Diagnostics;

namespace Treemap.Scanning;

/// <summary>
/// Scans a directory tree - or a whole NTFS volume - into immutable
/// <see cref="TreemapSnapshot"/> trees.
/// </summary>
/// <remarks>
/// <para>
/// The instance is a thin policy object: it owns the id map that keeps node identity stable
/// across rescans, and no per-scan state. Creating one per scan is fine too, at the cost of
/// ids restarting.
/// </para>
///
/// <para>
/// <b>Threading.</b> <see cref="ScanAsync"/> does all of its work - enumeration, tree
/// building and the publish - on thread-pool threads, so a UI host may call it straight
/// from a click handler. Progress reports arrive on a worker thread too.
/// </para>
///
/// <para>
/// <b>One scan at a time.</b> Calls are serialised: a second <see cref="ScanAsync"/> waits
/// rather than throwing, so a double-click on a Scan button queues instead of failing.
/// What it would be racing is node identity, which two walks would rebuild from different
/// pictures of the disk.
/// </para>
/// </remarks>
public sealed class FileSystemScanner
{
    private readonly ScanIdMap _ids = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly IScanBackend? _portableOverride;
    private readonly IScanBackend? _ntfsOverride;

    /// <summary>Creates a scanner that picks the best backend for each root.</summary>
    public FileSystemScanner()
    {
    }

    /// <summary>
    /// Test seam: substitutes a backend. A null override means "use the real one", so a
    /// test can replace only the backend it wants to make fail.
    /// </summary>
    internal FileSystemScanner(IScanBackend? portableOverride, IScanBackend? ntfsOverride)
    {
        _portableOverride = portableOverride;
        _ntfsOverride = ntfsOverride;
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/>, optionally publishing the finished tree.
    /// </summary>
    /// <param name="rootPath">Directory to scan. A volume root such as <c>C:\</c> scans the whole volume.</param>
    /// <param name="publisher">
    /// When supplied, the finished tree is published to it before this method returns,
    /// on a worker thread.
    /// </param>
    /// <param name="options">Bounds and policy. Defaults to <see cref="ScanOptions.Default"/>.</param>
    /// <param name="progress">Receives throttled progress on a worker thread.</param>
    /// <param name="cancellationToken">
    /// Cancellation observed before publication throws <see cref="OperationCanceledException"/>
    /// and leaves the publisher unchanged. It never triggers a backend fallback. A request
    /// made after publication may arrive too late to cancel the completed scan.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="rootPath"/> is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="DirectoryNotFoundException">The root does not exist.</exception>
    /// <exception cref="IOException">The root is not a readable directory, or is a link.</exception>
    /// <exception cref="UnauthorizedAccessException">The root cannot be read.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.</exception>
    public Task<ScanResult> ScanAsync(
        string rootPath,
        SnapshotPublisher? publisher = null,
        ScanOptions? options = null,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Validated before anything is queued, so a bad option fails at the call site rather
        // than surprising the caller with a faulted Task later.
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();

        ScanOptions validated = (options ?? ScanOptions.Default).Validate();
        return ScanExclusiveAsync(rootPath, publisher, validated, progress, cancellationToken);
    }

    private async Task<ScanResult> ScanExclusiveAsync(
        string rootPath,
        SnapshotPublisher? publisher,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Task.Run without the token on purpose: an already-cancelled call should reach
            // the scanner's own cancellation checks and throw from them, rather than
            // becoming a task that never ran.
            return await Task.Run(
                () => Scan(rootPath, publisher, options, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>
    /// Ready volumes, for a picker. Removable or network drive queries can be slow;
    /// UI hosts should use <see cref="GetReadyVolumesAsync"/>.
    /// </summary>
    public static IReadOnlyList<ScannableVolume> GetReadyVolumes()
    {
        List<ScannableVolume> volumes = [];

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return volumes;
        }

        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                volumes.Add(new ScannableVolume(
                    drive.Name,
                    drive.RootDirectory.FullName,
                    drive.DriveType,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
            {
                // A removable drive that vanished mid-enumeration is not worth reporting.
            }
        }

        return volumes;
    }

    /// <summary><see cref="GetReadyVolumes"/> on a worker thread.</summary>
    public static Task<IReadOnlyList<ScannableVolume>> GetReadyVolumesAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => GetReadyVolumes(), cancellationToken);

    private ScanResult Scan(
        string rootPath,
        SnapshotPublisher? publisher,
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        ScanCollector collector = new(options, progress);

        // One spelling for the whole scan, so a trailing separator cannot change the
        // reported root, the free-space check or the tree the caller gets back.
        string fullRootPath = ScanPath.Normalize(rootPath);
        ScanRequest request = new(fullRootPath, options, cancellationToken);

        IScanBackend portable = _portableOverride ?? new PortableScanBackend();

        ScanBackend? abandoned = null;
        string? fallbackReason = null;
        ScanBackend backend = ScanBackend.Portable;
        bool needPortableScan = true;

        collector.Report(ScanPhase.Starting, fullRootPath, force: true);
        cancellationToken.ThrowIfCancellationRequested();

        if (options.Backend != ScanBackendPreference.Portable)
        {
            IScanBackend? fast = CreateFastBackend();

            if (fast is null)
            {
                abandoned = ScanBackend.NtfsMft;
                fallbackReason = "The NTFS MFT backend is only available on Windows.";
            }
            else if (fast.TryRun(request, collector, out string? reason))
            {
                backend = fast.Id;
                needPortableScan = false;
            }
            else
            {
                // Whatever the fast backend collected is worthless now - it may be a partial
                // walk of a volume we have decided not to trust.
                abandoned = fast.Id;
                fallbackReason = reason;
                collector.Reset();
            }
        }

        // Checked before the fallback so a cancelled fast path cannot be mistaken for a
        // failed one and quietly restarted.
        cancellationToken.ThrowIfCancellationRequested();

        if (needPortableScan)
        {
            collector.Report(ScanPhase.Enumerating, fullRootPath, force: true);
            cancellationToken.ThrowIfCancellationRequested();

            if (!portable.TryRun(request, collector, out string? portableReason))
            {
                fallbackReason = Combine(fallbackReason, portableReason);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        collector.Report(ScanPhase.Building, fullRootPath, force: true);
        cancellationToken.ThrowIfCancellationRequested();

        _ids.BeginBuild();

        TreemapNode root;
        long freeSpace = 0;

        try
        {
            root = ScanTreeBuilder.Build(fullRootPath, collector.Entries, _ids, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Free space belongs to a volume, so it is only attached when the scan covered
            // the whole volume and finished. Padding a folder's treemap with the disk's
            // free bytes would overstate it by the size of the disk.
            if (options.IncludeFreeSpace && ScanTreeBuilder.IsVolumeRoot(fullRootPath) && collector.IsComplete)
            {
                root = AddFreeSpace(root, fullRootPath, collector, out freeSpace);
            }

            TreemapSnapshot? snapshot = null;
            if (publisher is not null)
            {
                collector.Report(ScanPhase.Publishing, null, force: true);
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = publisher.Publish(root);
            }

            collector.Report(ScanPhase.Finished, null, force: true);

            // Only now does this build become the identity the next scan starts from. A
            // cancelled or failed scan aborts its build instead, so a half-read tree cannot
            // overwrite the mapping the last good tree established.
            _ids.Commit();

            return new ScanResult
            {
                RootPath = fullRootPath,
                Root = root,
                Snapshot = snapshot,
                Backend = backend,
                FallbackFrom = abandoned,
                FallbackReason = fallbackReason,
                IsComplete = collector.IsComplete,
                IncompleteReason = collector.IncompleteReason,
                FileCount = collector.FileCount,
                DirectoryCount = collector.DirectoryCount,
                TotalLogicalSize = collector.LogicalBytes,
                TotalPhysicalSize = collector.PhysicalBytes,
                FreeSpaceBytes = freeSpace,
                HardLinkedFileCount = collector.HardLinkedFileCount,
                Diagnostics = collector.Diagnostics,
                SuppressedDiagnostics = collector.SuppressedDiagnostics,
                Duration = clock.Elapsed,
            };
        }
        catch
        {
            _ids.Abort();
            throw;
        }
    }

    /// <summary>
    /// The fast backend is constructed on Windows only, and only when the caller did not
    /// ask for the portable scanner, so every other platform reaches the portable path
    /// without a native call being made at all.
    /// </summary>
    private IScanBackend? CreateFastBackend()
    {
        if (_ntfsOverride is not null)
        {
            return _ntfsOverride;
        }

        return OperatingSystem.IsWindows() ? new NtfsMftScanBackend() : null;
    }

    /// <summary>
    /// Attaches the volume's free space as a last child block, or returns
    /// <paramref name="root"/> untouched when the platform will not say. Free space is not
    /// file content, so it joins the physical total only, and the figure is a point-in-time
    /// reading rather than something the scan walked.
    /// </summary>
    private TreemapNode AddFreeSpace(TreemapNode root, string rootPath, ScanCollector collector, out long freeSpace)
    {
        freeSpace = 0;

        string? volumeRoot = Path.GetPathRoot(ScanPath.Normalize(rootPath));
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return root;
        }

        try
        {
            freeSpace = new DriveInfo(volumeRoot).AvailableFreeSpace;
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            collector.AddDiagnostic(
                volumeRoot,
                $"Free space unavailable: {ScanFailure.Describe(exception)}",
                ScanDiagnosticSeverity.Warning);
            freeSpace = 0;
            return root;
        }

        if (freeSpace <= 0)
        {
            freeSpace = 0;
            return root;
        }

        TreemapNode[] children = new TreemapNode[root.Children.Count + 1];
        for (int i = 0; i < root.Children.Count; i++)
        {
            children[i] = root.Children[i];
        }

        children[^1] = TreemapNode.Create(
            _ids.ReserveFreeSpace(rootPath),
            "Free space",
            NodeKind.FreeSpace,
            freeSpace,
            freeSpace);

        return TreemapNode.Create(
            root.Id,
            root.Name,
            root.Kind,
            root.LogicalSize,
            root.PhysicalSize > long.MaxValue - freeSpace ? long.MaxValue : root.PhysicalSize + freeSpace,
            root.LastWriteTime,
            root.ColorGroup,
            children);
    }

    private static string? Combine(string? first, string? second)
        => string.IsNullOrEmpty(second) ? first : string.IsNullOrEmpty(first) ? second : first + "; " + second;
}
