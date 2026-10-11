using System.Diagnostics;
using System.Runtime.ExceptionServices;

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
    private readonly Func<string, VolumeSpaceInfo?> _volumeSpaceProvider;

    /// <summary>Creates a scanner that picks the best backend for each root.</summary>
    public FileSystemScanner()
    {
        _volumeSpaceProvider = ReadVolumeSpace;
    }

    /// <summary>
    /// Test seam: substitutes a backend. A null override means "use the real one", so a
    /// test can replace only the backend it wants to make fail.
    /// </summary>
    internal FileSystemScanner(
        IScanBackend? portableOverride,
        IScanBackend? ntfsOverride,
        Func<string, VolumeSpaceInfo?>? volumeSpaceProvider = null)
    {
        _portableOverride = portableOverride;
        _ntfsOverride = ntfsOverride;
        _volumeSpaceProvider = volumeSpaceProvider ?? ReadVolumeSpace;
    }

    /// <summary>
    /// Scans <paramref name="rootPath"/>, optionally publishing the finished tree.
    /// </summary>
    /// <param name="rootPath">Directory to scan. A volume root such as <c>C:\</c> scans the whole volume.</param>
    /// <param name="publisher">
    /// When supplied, the finished tree is published to it before this method returns,
    /// on a worker thread. With
    /// <see cref="ScanOptions.PublishIntermediateSnapshots"/> it also receives throttled
    /// work-in-progress trees while the scan runs; a cancelled or failed scan then
    /// restores the previously published map.
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

        bool isVolumeRoot = ScanTreeBuilder.IsVolumeRoot(fullRootPath);

        bool wantPreviews = options.PublishIntermediateSnapshots && publisher is not null;

        // Volume accounting is read once and cached: every preview and the final tree
        // measure free and unaccounted space against the same basis. Folder scans never
        // query capacity. A volume scan reads it only when progress, previews, or final
        // accounting need it; progress then reuses the same cached basis.
        object volumeGate = new();
        VolumeSpaceInfo? volumeSpace = null;
        bool volumeSpaceRead = false;
        VolumeSpaceInfo? GetVolumeSpace()
        {
            lock (volumeGate)
            {
                if (!volumeSpaceRead)
                {
                    volumeSpaceRead = true;
                    volumeSpace = _volumeSpaceProvider(fullRootPath);
                }

                return volumeSpace;
            }
        }

        if (isVolumeRoot && progress is not null)
        {
            // Folders never reach here. Unavailable capacity leaves the total null
            // (marquee); a clamped zero total reports 0. Shares the cached read the
            // previews and the final tree use.
            VolumeSpaceInfo? space = GetVolumeSpace();
            if (space is not null)
            {
                long total = space.TotalSize > 0 ? space.TotalSize : 0;
                long free = Math.Clamp(space.FreeSpaceBytes, 0, total);
                collector.UsedBytesForProgress = total <= free ? 0 : total - free;
            }
        }

        collector.Report(ScanPhase.Starting, fullRootPath, force: true);
        cancellationToken.ThrowIfCancellationRequested();

        // One identity transaction for the whole scan, opened before any backend runs:
        // previews reserve from the same pending map the final tree builds from, so an
        // id seen in a preview is the id the final tree and the next rescan use. Only
        // a finished scan commits; anything else aborts and keeps the last good map.
        _ids.BeginBuild();

        PreviewLoop? previews = null;
        TreemapSnapshot? snapshot = null;

        try
        {
            if (wantPreviews)
            {
                previews = new PreviewLoop(
                    collector, publisher!, _ids, fullRootPath, options, GetVolumeSpace, publisher!.Current, cancellationToken);

                // Synchronous initial basis on the scanning thread, before any backend
                // runs: an empty root with the cached free/unaccounted blocks. Later
                // previews replace the residual with discovered entries. Shares the
                // same pending identities and prior snapshot; the timer starts after.
                previews.PublishInitialBasis();
                previews.Start();
            }

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
                    // walk of a volume we have decided not to trust. The reset, the
                    // identity restart and the restore run under the preview gate, so
                    // an in-flight preview cannot capture abandoned entries after they
                    // are dropped, reserve their ids again, or publish over the
                    // restored map.
                    abandoned = fast.Id;
                    fallbackReason = reason;
                    if (previews is not null)
                    {
                        previews.AbandonAndRestore(collector);
                    }
                    else
                    {
                        collector.Reset();
                    }
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

            // Previews stop here, before this thread touches identity again: stopping
            // drains an in-flight build, so nothing can publish late over the final tree.
            // A preview that failed on the timer thread is rethrown here, on the scan,
            // before the final tree is built or published.
            previews?.Stop();
            previews?.ThrowIfFaulted();

            TreemapNode root = ScanTreeBuilder.Build(fullRootPath, collector.Entries, _ids, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            // Volume space belongs to a volume, so it is only attached when the scan
            // covered a whole volume root. Padding a folder's treemap with the disk's
            // bytes would overstate it by the size of the disk. Completeness is not
            // required: a partial volume scan still tiles what it found against the
            // same volume accounting.
            long freeSpace = 0;
            long unknownSpace = 0;

            if ((options.IncludeFreeSpace || options.IncludeUnknownSpace) && ScanTreeBuilder.IsVolumeRoot(fullRootPath))
            {
                VolumeSpaceInfo? space = GetVolumeSpace();
                if (space is null)
                {
                    collector.AddDiagnostic(
                        fullRootPath,
                        "Volume space is unavailable, so free and unaccounted space are not shown.",
                        ScanDiagnosticSeverity.Warning);
                }
                else
                {
                    root = AddVolumeSpace(
                        root, fullRootPath, space, collector.RepresentedBytes, options, _ids,
                        out freeSpace, out unknownSpace);
                }
            }

            if (publisher is not null)
            {
                collector.Report(ScanPhase.Publishing, null, force: true);
                cancellationToken.ThrowIfCancellationRequested();
                snapshot = publisher.Publish(root, fullRootPath);
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
                UnknownSpaceBytes = unknownSpace,
                HardLinkedFileCount = collector.HardLinkedFileCount,
                Diagnostics = collector.Diagnostics,
                SuppressedDiagnostics = collector.SuppressedDiagnostics,
                Duration = clock.Elapsed,
            };
        }
        catch
        {
            // Drain before aborting: an in-flight preview holds the loop gate while it
            // builds, and aborting first would pull the identity map out from under it.
            previews?.Stop();
            _ids.Abort();

            // Opt-in previews may have shown a half-read tree - a preview, or a final
            // publish whose subscribers threw after it became visible. Put the last
            // accepted map back so a cancelled or failed scan does not leave one on
            // screen. Best effort: the original failure is what the caller hears. A
            // finished final publish (snapshot set) is never undone, and without
            // opt-in nothing was published mid-scan, so the publisher is untouched.
            if (previews is not null && snapshot is null)
            {
                previews.RestorePriorIfChanged();
            }

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
    /// Attaches the volume's free and unaccounted space as trailing child blocks, or
    /// returns <paramref name="root"/> untouched when there is nothing to show. Neither
    /// block is file content, so they join the physical total only; the file totals on
    /// <see cref="ScanResult"/> are unaffected. A point-in-time reading, measured
    /// against the cached <paramref name="space"/> the previews used too.
    /// </summary>
    private static TreemapNode AddVolumeSpace(
        TreemapNode root,
        string fullRootPath,
        VolumeSpaceInfo space,
        long representedBytes,
        ScanOptions options,
        ScanIdMap ids,
        out long freeSpace,
        out long unknownSpace)
    {
        freeSpace = 0;
        unknownSpace = 0;

        // The provider is injectable and the platform can report nonsense, so the
        // basis is clamped here: free stays inside [0, total], and a non-positive
        // total yields no blocks at all.
        long total = space.TotalSize > 0 ? space.TotalSize : 0;
        long free = Math.Clamp(space.FreeSpaceBytes, 0, total);

        if (options.IncludeFreeSpace && free > 0)
        {
            freeSpace = free;
        }

        if (options.IncludeUnknownSpace)
        {
            unknownSpace = VolumeSpace.UnknownResidual(total, free, representedBytes);
        }

        if (freeSpace <= 0 && unknownSpace <= 0)
        {
            return root;
        }

        int extra = (freeSpace > 0 ? 1 : 0) + (unknownSpace > 0 ? 1 : 0);
        TreemapNode[] children = new TreemapNode[root.Children.Count + extra];
        for (int i = 0; i < root.Children.Count; i++)
        {
            children[i] = root.Children[i];
        }

        int at = root.Children.Count;
        if (freeSpace > 0)
        {
            children[at++] = TreemapNode.Create(
                ids.ReserveFreeSpace(fullRootPath),
                "Free space",
                NodeKind.FreeSpace,
                freeSpace,
                freeSpace);
        }

        if (unknownSpace > 0)
        {
            children[at++] = TreemapNode.Create(
                ids.ReserveUnknownSpace(fullRootPath),
                "Unaccounted space",
                NodeKind.UnknownSpace,
                unknownSpace,
                unknownSpace);
        }

        return TreemapNode.Create(
            root.Id,
            root.Name,
            root.Kind,
            root.LogicalSize,
            SaturatingAdd(SaturatingAdd(root.PhysicalSize, freeSpace), unknownSpace),
            root.LastWriteTime,
            root.ColorGroup,
            children);
    }

    /// <summary>Sums without wrapping: a total that has silently gone negative is worse than one that saturates.</summary>
    private static long SaturatingAdd(long running, long value)
    {
        long add = value > 0 ? value : 0;
        return running > long.MaxValue - add ? long.MaxValue : running + add;
    }

    /// <summary>
    /// The default volume accounting: live <see cref="DriveInfo"/> numbers for a whole
    /// volume root, null everywhere else - and null when the platform will not answer.
    /// Only a non-null answer marks the root as a volume for space purposes, so a
    /// directory scan can never grow space blocks.
    /// </summary>
    private static VolumeSpaceInfo? ReadVolumeSpace(string fullRootPath)
    {
        if (!ScanTreeBuilder.IsVolumeRoot(fullRootPath))
        {
            return null;
        }

        string? volumeRoot = Path.GetPathRoot(fullRootPath);
        if (string.IsNullOrEmpty(volumeRoot))
        {
            return null;
        }

        try
        {
            // TotalFreeSpace, not AvailableFreeSpace: quotas can shrink what is
            // available to one user without changing what is free on the volume.
            // Each property is read once; free is clamped to the volume in
            // AddVolumeSpace, which also covers injected test providers.
            DriveInfo drive = new(volumeRoot);
            long total = drive.TotalSize;
            long free = drive.TotalFreeSpace;
            return new VolumeSpaceInfo(total < 0 ? 0 : total, free < 0 ? 0 : free);
        }
        catch (Exception exception) when (ScanFailure.IsRecoverable(exception))
        {
            return null;
        }
    }

    /// <summary>
    /// Throttled intermediate publishes for one scan. A single-shot timer reschedules
    /// itself, so there is at most one outstanding build and never a queue: a tick that
    /// finds a build already running is skipped, and the next tick picks up newer data.
    /// A preview failure never faults the scan from the timer thread; it is captured
    /// and rethrown by <see cref="ThrowIfFaulted"/> on the scanning thread instead.
    /// </summary>
    /// <remarks>
    /// One plain <c>object</c> gate serves <c>lock</c>, <c>Monitor.TryEnter</c> and the
    /// drain in <see cref="Stop"/> alike: they are the same lock, so stopping really
    /// waits out an in-flight build. (Mixing those primitives with
    /// <c>System.Threading.Lock</c> would silently not exclude each other.)
    /// </remarks>
    private sealed class PreviewLoop
    {
        /// <summary>
        /// Smallest delay the timer is ever armed with. A zero or submillisecond
        /// <see cref="ScanOptions.SnapshotInterval"/> would otherwise spin the pool
        /// thread continuously, rebuilding on every tick.
        /// </summary>
        private static readonly TimeSpan MinSnapshotDelay = TimeSpan.FromMilliseconds(10);

        private readonly ScanCollector _collector;
        private readonly SnapshotPublisher _publisher;
        private readonly ScanIdMap _ids;
        private readonly string _fullRootPath;
        private readonly bool _isVolumeRoot;
        private readonly ScanOptions _options;
        private readonly Func<VolumeSpaceInfo?> _volumeSpace;
        private readonly TreemapSnapshot _prior;
        private readonly CancellationToken _cancellationToken;
        private readonly object _gate = new();
        private Timer? _timer;
        private long _published;
        private long _builtEntries;
        private bool _stopped;
        private bool _poisoned;
        private ExceptionDispatchInfo? _fault;

        public PreviewLoop(
            ScanCollector collector,
            SnapshotPublisher publisher,
            ScanIdMap ids,
            string fullRootPath,
            ScanOptions options,
            Func<VolumeSpaceInfo?> volumeSpace,
            TreemapSnapshot prior,
            CancellationToken cancellationToken)
        {
            _collector = collector;
            _publisher = publisher;
            _ids = ids;
            _fullRootPath = fullRootPath;
            _isVolumeRoot = ScanTreeBuilder.IsVolumeRoot(fullRootPath);
            _options = options;
            _volumeSpace = volumeSpace;
            _prior = prior;
            _cancellationToken = cancellationToken;
        }

        public void Start()
        {
            lock (_gate)
            {
                _timer = new Timer(Callback, null, DueDelay(), Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>
        /// Publishes the initial volume basis synchronously on the scanning thread,
        /// before any backend runs: an empty root with the cached free/unaccounted
        /// blocks. Shares the scan's pending identity transaction and prior snapshot,
        /// so the free/unknown ids here are the ids the final tree uses. Only enabled
        /// space blocks participate; folder scans, final-only callers and unavailable
        /// capacity publish nothing. The timer still starts after this returns.
        /// A throwing subscriber propagates after the snapshot is visible, and the
        /// scan's catch restores the prior map and aborts identity.
        /// </summary>
        public void PublishInitialBasis()
        {
            lock (_gate)
            {
                PublishInitialBasisLocked();
            }
        }

        private void PublishInitialBasisLocked()
        {
            if (_stopped || _poisoned)
            {
                return;
            }

            _cancellationToken.ThrowIfCancellationRequested();

            TreemapNode? initial = BuildInitialRoot();
            if (initial is null)
            {
                return;
            }

            _cancellationToken.ThrowIfCancellationRequested();

            long before = _publisher.Current.Version;
            try
            {
                _publisher.Publish(initial, _fullRootPath);
            }
            finally
            {
                // Visible before subscribers run, so a throwing subscriber still
                // left this map on screen: count it so the catch restores.
                // _builtEntries stays 0 - no entries yet, so the first real
                // preview still fires and empty trees never republish.
                if (_publisher.Current.Version != before)
                {
                    Interlocked.Increment(ref _published);
                }
            }
        }

        /// <summary>
        /// Empty root plus space blocks against represented zero, or null when there
        /// is no volume basis to show. Uses the shared cached capacity, never disk IO.
        /// </summary>
        private TreemapNode? BuildInitialRoot()
        {
            if (!_isVolumeRoot)
            {
                return null;
            }

            if (!_options.IncludeFreeSpace && !_options.IncludeUnknownSpace)
            {
                return null;
            }

            VolumeSpaceInfo? space = _volumeSpace();
            if (space is null)
            {
                return null;
            }

            _cancellationToken.ThrowIfCancellationRequested();

            TreemapNode empty = ScanTreeBuilder.Build(
                _fullRootPath, Array.Empty<ScanEntry>(), _ids, _cancellationToken);
            TreemapNode withSpace = AddVolumeSpace(
                empty, _fullRootPath, space, 0, _options, _ids,
                out long freeSpace, out long unknownSpace);

            if (freeSpace <= 0 && unknownSpace <= 0)
            {
                return null;
            }

            return withSpace;
        }

        /// <summary>
        /// Stops the timer and waits out an in-flight build. Idempotent, and never
        /// throws: by the time it returns, no preview from this scan can still publish.
        /// </summary>
        /// <remarks>
        /// <see cref="Timer.Dispose()"/> does not wait for a running or already-queued
        /// callback; the shared gate does that. The first lock waits out an in-flight
        /// build, and setting <c>_stopped</c> under that same gate makes any callback
        /// that acquires it afterwards return without publishing. Re-acquiring the
        /// gate after disposing drains such a queued callback.
        /// </remarks>
        public void Stop()
        {
            Timer? timer;
            lock (_gate)
            {
                if (_stopped)
                {
                    return;
                }

                _stopped = true;
                timer = _timer;
                _timer = null;
            }

            try
            {
                timer?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }

            lock (_gate)
            {
            }
        }

        /// <summary>
        /// Rethrows the first preview failure on the scanning thread, preserving its
        /// stack. Called after <see cref="Stop"/>, before the final build: a transient
        /// subscriber fault is a scan failure the caller hears about, not a poison flag
        /// the final publish silently swallows.
        /// </summary>
        public void ThrowIfFaulted()
        {
            ExceptionDispatchInfo? fault;
            lock (_gate)
            {
                fault = _fault;
            }

            fault?.Throw();
        }

        /// <summary>
        /// Drops everything the abandoned fast backend collected, forgets the ids it
        /// reserved, and puts the map from before the scan back on screen. The reset,
        /// the identity restart and the count reset run under the gate as one step,
        /// so an in-flight preview can neither capture abandoned entries after they
        /// are dropped nor publish over the restored map. For a volume scan the
        /// initial empty-plus-space basis is then republished for the portable retry,
        /// serially under the same gate.
        /// </summary>
        public void AbandonAndRestore(ScanCollector collector)
        {
            lock (_gate)
            {
                collector.Reset();

                if (Interlocked.Read(ref _published) > 0 && !_stopped)
                {
                    try
                    {
                        _publisher.Publish(_prior.Root, _prior.RootPath);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        _fault ??= ExceptionDispatchInfo.Capture(exception);
                        _poisoned = true;
                    }
                }

                Interlocked.Exchange(ref _published, 0);
                _builtEntries = 0;

                // Reservations the abandoned walk made are forgotten: only paths in
                // returned trees carry over, so a path seen only in the discarded
                // preview gets a fresh id if a later scan shows it again, while
                // paths in the last accepted tree keep their committed ids.
                _ids.BeginBuild();

                // Same basis the scan opened with. On the scanning thread, so a
                // throwing subscriber propagates to the scan's catch, which restores
                // and aborts; timer faults are still captured on the pool thread.
                PublishInitialBasisLocked();
            }
        }

        /// <summary>
        /// Puts the map from before the scan back on screen when the scan made the
        /// publisher move - through a preview, or through a final publish whose
        /// subscribers threw after it became visible. Best effort: failures here must
        /// not replace the failure already unwinding on the scanning thread.
        /// </summary>
        public void RestorePriorIfChanged()
        {
            lock (_gate)
            {
                if (_publisher.Current.Version == _prior.Version)
                {
                    return;
                }

                try
                {
                    _publisher.Publish(_prior.Root, _prior.RootPath);
                }
                catch
                {
                }
            }
        }

        private TimeSpan DueDelay()
            => _options.SnapshotInterval < MinSnapshotDelay ? MinSnapshotDelay : _options.SnapshotInterval;

        private void Callback(object? state)
        {
            // A build already running means this tick is stale before it starts: skip it
            // rather than queueing, and let the next tick see newer data.
            if (!Monitor.TryEnter(_gate))
            {
                Reschedule();
                return;
            }

            try
            {
                if (_stopped || _poisoned)
                {
                    return;
                }

                _cancellationToken.ThrowIfCancellationRequested();

                // The count is read first so an unchanged entry set costs no tree copy.
                int count = _collector.EntryCount;
                if (count == 0 || count == _builtEntries)
                {
                    return;
                }

                (ScanEntry[] entries, long represented) = _collector.CapturePreview();

                // Empty trees carry no information, and an unchanged entry set means
                // the same tree as last time: publish neither, and never repeatedly.
                if (entries.Length == 0 || entries.Length == _builtEntries)
                {
                    return;
                }

                TreemapNode root = ScanTreeBuilder.Build(_fullRootPath, entries, _ids, _cancellationToken);

                // Volume metadata is read only for volume roots, and only when the
                // entry set actually changed: a folder scan never touches the provider.
                if (_isVolumeRoot)
                {
                    VolumeSpaceInfo? space = _volumeSpace();
                    if (space is not null)
                    {
                        root = AddVolumeSpace(
                            root, _fullRootPath, space, represented, _options, _ids,
                            out _, out _);
                    }
                }

                if (_stopped)
                {
                    return;
                }

                _cancellationToken.ThrowIfCancellationRequested();

                long before = _publisher.Current.Version;
                try
                {
                    _publisher.Publish(root, _fullRootPath);
                }
                finally
                {
                    // The snapshot is visible before subscribers run, so a throwing
                    // subscriber still left this map on screen: count it as published
                    // so cancellation and failure restore the prior map.
                    if (_publisher.Current.Version != before)
                    {
                        _builtEntries = entries.Length;
                        Interlocked.Increment(ref _published);
                    }
                }
            }
            catch (OperationCanceledException exception) when (!_cancellationToken.IsCancellationRequested)
            {
                // A subscriber threw OperationCanceledException while the scan itself
                // is not cancelling: that is a fault, not a cancellation. Capture it
                // like any other preview failure so the scan fails and restores.
                _fault ??= ExceptionDispatchInfo.Capture(exception);
                _poisoned = true;
            }
            catch (OperationCanceledException)
            {
                // The scan is unwinding; it stops this loop and restores the prior map.
            }
            catch (Exception exception)
            {
                // Never fault the scan from a pool thread. Record the first failure and
                // stop previewing; the scanning thread rethrows it before the final
                // build, then restores the prior map and aborts identity.
                _fault ??= ExceptionDispatchInfo.Capture(exception);
                _poisoned = true;
            }
            finally
            {
                Monitor.Exit(_gate);
                Reschedule();
            }
        }

        private void Reschedule()
        {
            Timer? timer;
            bool stopped;
            bool poisoned;

            lock (_gate)
            {
                timer = _timer;
                stopped = _stopped;
                poisoned = _poisoned;
            }

            if (timer is null || stopped || poisoned)
            {
                return;
            }

            try
            {
                timer.Change(DueDelay(), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static string? Combine(string? first, string? second)
        => string.IsNullOrEmpty(second) ? first : string.IsNullOrEmpty(first) ? second : first + "; " + second;
}
