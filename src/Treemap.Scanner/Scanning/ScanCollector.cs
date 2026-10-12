using System.Diagnostics;

namespace Treemap.Scanning;

/// <summary>Accumulates what a backend found, and owns every bound the scan works under.</summary>
/// <remarks>
/// <para>
/// The collector, not the backend, holds the limits. Both backends draw on one counter, so
/// a directory, a file and a diagnostic all cost the same thing. When it runs out the
/// collector stops accepting work and the result is marked incomplete - a scan is never
/// silently cut short.
/// </para>
///
/// <para>
/// The portable backend fills this from several workers, so every mutation is under one
/// gate: the budget claim, the list append and the counters happen together or not at all.
/// Without that, two workers can both see room for the last entry and overshoot together.
/// Progress is the one thing that leaves the gate - the record is built under the lock and
/// the caller's callback runs outside it, so a callback that blocks or cancels cannot stall
/// or deadlock the workers.
/// </para>
///
/// <para>
/// <see cref="Reset"/> exists for the fallback path: partial data from an abandoned backend
/// must not leak into the portable retry.
/// </para>
/// </remarks>
internal sealed class ScanCollector
{
    private readonly ScanOptions _options;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private readonly long _memoryLimit;
    private readonly int _rootPathLength;

    /// <summary>Native table walks: folders first, entries second. The bar spans both.</summary>
    internal const int MftPassCount = 2;

    /// <summary>
    /// Incremental directory tree for attached (production) scans: folder names stored once,
    /// files keep basename plus parent linkage, immutable file nodes created once and shared
    /// by every preview and the final tree. Unused until <see cref="AttachIds"/> is called.
    /// </summary>
    private readonly ManagedScanTree _tree = new();

    /// <summary>
    /// Flat entry list for direct (test/small) use without attached ids. Preserves exact
    /// legacy semantics including duplicate emissions; production scans never fill this.
    /// </summary>
    private readonly List<ScanEntry> _entries = [];
    private ScanIdMap? _ids;
    private readonly List<ScanDiagnostic> _diagnostics = [];

    private long _files;
    private long _directories;
    private long _logicalBytes;
    private long _physicalBytes;
    private long _representedBytes;
    private long _hardLinkedFiles;
    private long _suppressedDiagnostics;
    private long _budgetUsed;
    private long _estimatedMemoryBytes;
    private bool _memoryLimitReached;
    private long _nextReportAt;
    private ScanBackend? _activeBackend;
    private ScanBackend? _fallbackFrom;
    private string? _fallbackReason;
    private long? _mftTotalBytes;
    private int _mftPassIndex = -1;
    private long _mftBytesScanned;
    private long _mftPassStartMs;
    private string? _incompleteReason;
    private bool _allocationUnavailable;
    private bool _complete = true;

    public ScanCollector(ScanOptions options, IProgress<ScanProgress>? progress, int rootPathLength = 0)
    {
        _options = options;
        _progress = progress;
        _memoryLimit = ScanMemoryBudget.ResolveLimit(options);
        _rootPathLength = rootPathLength;
    }

    /// <summary>
    /// Attaches the scan's identity map, switching this collector to the incremental tree
    /// path: file/folder ids are reserved at add time and file nodes are created once and
    /// shared by every preview and the final tree. Called once by FileSystemScanner after
    /// BeginBuild, before any backend runs (the tree is empty then). Collectors used
    /// directly by tests never attach and keep exact legacy list semantics.
    /// </summary>
    public void AttachIds(ScanIdMap ids)
    {
        lock (_gate)
        {
            _ids = ids;
        }
    }

    /// <summary>
    /// Below-root entries. Without attached ids this is the legacy flat list (duplicates
    /// preserved); with ids it is reconstructed from the tree for small-tree and test use.
    /// Production builds never call this for large volumes; they freeze the tree directly
    /// so no full-path-per-file copy is ever materialised.
    /// </summary>
    public IReadOnlyList<ScanEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _ids is null ? _entries.ToArray() : [.. _tree.EnumerateEntries()];
            }
        }
    }

    public IReadOnlyList<ScanDiagnostic> Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    public int SuppressedDiagnostics
    {
        get
        {
            lock (_gate)
            {
                return (int)Math.Min(int.MaxValue, _suppressedDiagnostics);
            }
        }
    }

    public long FileCount
    {
        get
        {
            lock (_gate)
            {
                return _files;
            }
        }
    }

    public long DirectoryCount
    {
        get
        {
            lock (_gate)
            {
                return _directories;
            }
        }
    }

    public long LogicalBytes
    {
        get
        {
            lock (_gate)
            {
                return _logicalBytes;
            }
        }
    }

    public long PhysicalBytes
    {
        get
        {
            lock (_gate)
            {
                return _physicalBytes;
            }
        }
    }

    /// <summary>
    /// Sum of the per-file layout weights: the allocated size where it is known and
    /// positive, otherwise the apparent size. This is what the treemap actually draws
    /// for each file (see <see cref="TreemapNode.LayoutSize"/>), so it is the basis the
    /// unaccounted-space residual is measured against.
    /// </summary>
    public long RepresentedBytes
    {
        get
        {
            lock (_gate)
            {
                return _representedBytes;
            }
        }
    }

    public long HardLinkedFileCount
    {
        get
        {
            lock (_gate)
            {
                return _hardLinkedFiles;
            }
        }
    }

    /// <summary>
    /// Cached used-byte total for volume progress. Set once before enumeration; null
    /// means unknown. <see cref="Report"/> returns 100 for <see cref="ScanPhase.Finished"/>
    /// regardless, null for an unavailable total, 0 for an empty used-byte total,
    /// otherwise 0..99.
    /// </summary>
    public long? UsedBytesForProgress { get; set; }

    /// <summary>Sets the backend triple under one gate so a tick never sees a partial update.</summary>
    public void SetBackend(ScanBackend? activeBackend, ScanBackend? fallbackFrom, string? fallbackReason)
    {
        lock (_gate)
        {
            _activeBackend = activeBackend;
            _fallbackFrom = fallbackFrom;
            _fallbackReason = fallbackReason;

            // A portable retry must never show stale table position or ETA: the table
            // walk is over and the byte basis is different.
            if (fallbackFrom.HasValue || activeBackend != ScanBackend.NtfsMft)
            {
                ClearMftLocked();
            }
        }
    }

    /// <summary>
    /// Known MFT table span for the current scan, in bytes. Set once per native run before
    /// either pass; a non-positive total clears back to unknown. Caller holds no gate;
    /// the gate is taken here.
    /// </summary>
    public void SetMftTraversal(long totalBytes)
    {
        lock (_gate)
        {
            _mftTotalBytes = totalBytes > 0 ? totalBytes : null;
            _mftPassIndex = -1;
            _mftBytesScanned = 0;
        }
    }

    /// <summary>
    /// Starts one native pass (0 for folders, 1 for entries) and forces a boundary tick at
    /// zero so the bar never sits still while the table moves. Resets the pass clock, so the
    /// remaining-time estimate restarts for the new pass.
    /// </summary>
    public void BeginMftPass(int passIndex, ScanPhase phase, string? currentPath)
    {
        if (passIndex < 0 || passIndex >= MftPassCount)
        {
            throw new ArgumentOutOfRangeException(nameof(passIndex), passIndex, $"Must be between 0 and {MftPassCount - 1}.");
        }

        lock (_gate)
        {
            if (_mftTotalBytes is null)
            {
                return;
            }

            _mftPassIndex = passIndex;
            _mftBytesScanned = 0;
            _mftPassStartMs = _clock.ElapsedMilliseconds;
        }

        ReportMft(phase, currentPath, 0, force: true);
    }

    /// <summary>Throttled native progress for the current pass, reported outside the gate.</summary>
    public void ReportMft(ScanPhase phase, string? currentPath, long bytesScanned, bool force = false)
    {
        IProgress<ScanProgress>? progress = _progress;
        if (progress is null)
        {
            lock (_gate)
            {
                if (_mftTotalBytes is not null && _mftPassIndex >= 0)
                {
                    _mftBytesScanned = Math.Clamp(bytesScanned, 0, _mftTotalBytes.Value);
                }
            }

            return;
        }

        long entries;
        long logical;
        ScanBackend? activeBackend;
        ScanBackend? fallbackFrom;
        string? fallbackReason;
        long total;
        long scanned;
        int passIndex;
        TimeSpan? remaining;

        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!force && now < _nextReportAt)
            {
                return;
            }

            if (_mftTotalBytes is null || _mftPassIndex < 0)
            {
                return;
            }

            _nextReportAt = now + (long)_options.ProgressInterval.TotalMilliseconds;
            entries = _ids is null ? _entries.Count : checked((int)Math.Min(int.MaxValue, _files + _directories));
            logical = _logicalBytes;
            activeBackend = _activeBackend;
            fallbackFrom = _fallbackFrom;
            fallbackReason = _fallbackReason;
            total = _mftTotalBytes.Value;
            passIndex = _mftPassIndex;
            scanned = Math.Clamp(bytesScanned, 0, total);
            _mftBytesScanned = scanned;

            double fraction = total <= 0 ? 0 : (double)scanned / (double)total;
            TimeSpan elapsed = TimeSpan.FromMilliseconds(Math.Max(0, now - _mftPassStartMs));
            remaining = EstimateMftRemaining(elapsed, fraction);
        }

        double done = total <= 0
            ? 0
            : Math.Clamp((double)scanned / (double)total, 0, 1);
        int percent = Math.Clamp((int)(((passIndex + done) / MftPassCount) * 100.0), 0, 99);

        ScanProgress tick = new(phase, currentPath, entries, logical)
        {
            PercentComplete = percent,
            ActiveBackend = activeBackend,
            FallbackFrom = fallbackFrom,
            FallbackReason = fallbackReason,
            MftBytesScanned = scanned,
            MftTotalBytes = total,
            EstimatedRemainingTime = remaining,
        };

        progress.Report(tick);
    }

    /// <summary>
    /// Approximate remaining time for the current pass from observed throughput. Pure math,
    /// so tests pass an explicit elapsed rather than sleeping. Null until about a second
    /// has passed with positive but incomplete progress; null on zero, complete, infinite,
    /// negative or overflowing inputs.
    /// </summary>
    internal static TimeSpan? EstimateMftRemaining(TimeSpan elapsed, double fraction)
    {
        if (elapsed < TimeSpan.FromSeconds(1))
        {
            return null;
        }

        if (double.IsNaN(fraction) || double.IsInfinity(fraction) || fraction <= 0 || fraction >= 1)
        {
            return null;
        }

        double seconds = elapsed.TotalSeconds * (1.0 - fraction) / fraction;

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromSeconds(seconds);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>False as soon as anything failed, was truncated or was approximated.</summary>
    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _complete;
            }
        }
    }

    public string? IncompleteReason
    {
        get
        {
            lock (_gate)
            {
                return _incompleteReason;
            }
        }
    }

    /// <summary>
    /// True once the shared budget is spent, and marks the result incomplete when it is: a
    /// walk that stops here left something unseen. Callers stop walking and stop draining
    /// the queue when it is true.
    /// </summary>
    public bool ShouldStop()
    {
        lock (_gate)
        {
            if (_memoryLimitReached || _estimatedMemoryBytes >= _memoryLimit)
            {
                MarkMemoryLimitLocked();
                return true;
            }

            if (_budgetUsed < _options.MaxEntries)
            {
                return false;
            }

            MarkIncompleteLocked($"Stopped after {_options.MaxEntries} entries, the configured budget.");
            return true;
        }
    }

    /// <summary>
    /// Records one entry. Returns false once the budget is spent, which tells a backend to
    /// stop walking rather than keep paying for entries nobody will see.
    /// </summary>
    /// <remarks>
    /// False is a reason to stop, never a reason to try another backend - the budget applies
    /// to the portable retry too.
    /// </remarks>
    /// <remarks>
    /// True for the second and later names a hard-linked file appears under, so
    /// <see cref="HardLinkedFileCount"/> is the number of extra nodes rather than of files.
    /// </remarks>
    /// <summary>
    /// Legacy flat-list append for unattached collectors. Byte-for-byte the old behaviour:
    /// one shared-budget unit plus the legacy path estimate per emission, duplicates kept.
    /// </summary>
    private bool TryAddLegacy(
        string relativePath,
        NodeKind kind,
        long logicalSize,
        long physicalSize,
        DateTimeOffset? lastWriteTime,
        bool isExtraHardLinkEntry)
    {
        lock (_gate)
        {
            long cost = ScanMemoryBudget.EstimateEntry(relativePath);
            if (kind == NodeKind.Folder)
            {
                // Portable discovery retains absolute paths for queued folders.
                cost += 2L * _rootPathLength;
            }

            if (!ClaimLocked(cost))
            {
                return false;
            }

            _entries.Add(new ScanEntry(relativePath, kind, logicalSize, physicalSize, lastWriteTime));

            if (kind == NodeKind.Folder)
            {
                _directories++;
                return true;
            }

            _files++;
            _logicalBytes = SaturatingAdd(_logicalBytes, logicalSize);
            _physicalBytes = SaturatingAdd(_physicalBytes, physicalSize);
            _representedBytes = SaturatingAdd(_representedBytes, physicalSize > 0 ? physicalSize : logicalSize);

            if (isExtraHardLinkEntry)
            {
                _hardLinkedFiles++;
            }

            return true;
        }
    }

    public bool TryAdd(
        string relativePath,
        NodeKind kind,
        long logicalSize,
        long physicalSize,
        DateTimeOffset? lastWriteTime,
        bool isExtraHardLinkEntry = false)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        // Unattached collectors (direct unit-test and NTFS-seam use) keep exact legacy list
        // semantics: every accepted emission is appended, duplicates included, charged with
        // the legacy estimate. Attached production scans use the incremental tree below.
        // The attach happens-before any backend task starts, so this lock-free read is safe.
        if (_ids is null)
        {
            return TryAddLegacy(relativePath, kind, logicalSize, physicalSize, lastWriteTime, isExtraHardLinkEntry);
        }

        string[] segments = ManagedScanTree.Split(relativePath);
        if (segments.Length == 0)
        {
            return true;
        }

        bool leafIsFolder = kind == NodeKind.Folder;
        string leafName = segments[^1];

        lock (_gate)
        {
            // Honest charging: the leaf plus only the parent folders this entry actually
            // creates. Siblings sharing parents pay for the leaf alone; inferred parents
            // are never recharged. A folder leaf already recorded (inferred or explicit)
            // costs no second retained folder - the budget unit is still claimed so the
            // MaxEntries count matches the legacy flat list, but no new folder bytes.
            // Folder entries also carry transient absolute-path workspace while the
            // portable queue holds them; duplicates from scripted order have none.
            long parentCost = 0;
            foreach (int missingLength in _tree.MissingFolderLengths(segments))
            {
                parentCost += ScanMemoryBudget.EstimateFolder(missingLength);
            }

            long cost;
            if (leafIsFolder)
            {
                var peek = _tree.TryFindParent(segments);
                bool exists = peek.Found && !peek.Blocked && peek.Parent is not null
                    && ManagedScanTree.HasFolderLeaf(peek.Parent, leafName);
                cost = parentCost + (exists ? 0 : ScanMemoryBudget.EstimateFolder(leafName.Length) + (2L * _rootPathLength));
            }
            else
            {
                cost = parentCost + ScanMemoryBudget.EstimateFile(leafName.Length);
            }

            if (!ClaimLocked(cost))
            {
                return false;
            }

            var (parent, _, _, blocked) = _tree.EnsureParents(segments, _ids);
            if (blocked)
            {
                // A file already owns an intermediate segment of this path (impossible on a
                // real filesystem: the portable backend emits a parent before its children
                // and never walks under a file, so this is scripted-order only). The
                // existing file wins, matching ScanTreeBuilder.Materialise which drops
                // children of file nodes - except the legacy builder keeps them on the
                // mutable node and would resurrect them if a later folder entry flips the
                // leaf kind, while this tree drops blocked descendants permanently. No
                // reconciliation: the counters below preserve legacy per-emission counts,
                // but the tree never nests under a file.
                if (!leafIsFolder)
                {
                    _files++;
                    _logicalBytes = SaturatingAdd(_logicalBytes, logicalSize);
                    _physicalBytes = SaturatingAdd(_physicalBytes, physicalSize);
                    _representedBytes = SaturatingAdd(_representedBytes, physicalSize > 0 ? physicalSize : logicalSize);
                    if (isExtraHardLinkEntry)
                    {
                        _hardLinkedFiles++;
                    }
                }
                else
                {
                    _directories++;
                }

                return true;
            }

            if (leafIsFolder)
            {
                _tree.AddFolderLeaf(parent, leafName, lastWriteTime, _ids, out _);
                _directories++;
                return true;
            }

            _tree.AddFile(parent, leafName, logicalSize, physicalSize, lastWriteTime, _ids);
            _files++;
            _logicalBytes = SaturatingAdd(_logicalBytes, logicalSize);
            _physicalBytes = SaturatingAdd(_physicalBytes, physicalSize);
            _representedBytes = SaturatingAdd(_representedBytes, physicalSize > 0 ? physicalSize : logicalSize);

            if (isExtraHardLinkEntry)
            {
                _hardLinkedFiles++;
            }

            return true;
        }
    }

    /// <summary>
    /// Records a problem, bounded by <see cref="ScanOptions.MaxDiagnostics"/> and charged
    /// against the shared budget either way, so a suppressed diagnostic is still counted in
    /// <see cref="SuppressedDiagnostics"/>.
    /// </summary>
    /// <remarks>
    /// Only <see cref="ScanDiagnosticSeverity.Error"/> clears <see cref="IsComplete"/>: a
    /// warning records a deliberate policy that cost some detail, which is worth saying out
    /// loud without claiming the scan failed.
    /// </remarks>
    public void AddDiagnostic(string path, string message, ScanDiagnosticSeverity severity = ScanDiagnosticSeverity.Error)
    {
        lock (_gate)
        {
            AddDiagnosticLocked(path, message, severity);
        }
    }

    private void AddDiagnosticLocked(string path, string message, ScanDiagnosticSeverity severity)
    {
        if (severity == ScanDiagnosticSeverity.Error)
        {
            MarkIncompleteLocked(message);
        }

        long cost = _diagnostics.Count < _options.MaxDiagnostics
            ? ScanMemoryBudget.EstimateDiagnostic(path, message)
            : 0;
        if (!ClaimLocked(cost) || _diagnostics.Count >= _options.MaxDiagnostics)
        {
            _suppressedDiagnostics++;
            return;
        }

        _diagnostics.Add(new ScanDiagnostic(severity, path, message));
    }

    /// <summary>Notes a truncated or approximated scan without inventing a diagnostic.</summary>
    public void MarkIncomplete(string reason)
    {
        lock (_gate)
        {
            MarkIncompleteLocked(reason);
        }
    }

    /// <summary>
    /// Says once that allocated size could not be read, so a tree reporting
    /// <c>PhysicalSize == 0</c> is not mistaken for a tree of empty files. A warning, not an
    /// error: nothing was missed, and <see cref="TreemapNode.LayoutSize"/> already falls back
    /// to the logical size.
    /// </summary>
    public void NoteAllocationUnavailable(string reason)
    {
        lock (_gate)
        {
            if (_allocationUnavailable)
            {
                return;
            }

            _allocationUnavailable = true;
            AddDiagnosticLocked(string.Empty, reason, ScanDiagnosticSeverity.Warning);
        }
    }

    /// <summary>
    /// Throttled progress, reported outside the gate because the callback is caller code and
    /// may block or cancel the scan.
    /// </summary>
    public void Report(ScanPhase phase, string? currentPath, bool force = false)
    {
        IProgress<ScanProgress>? progress = _progress;
        if (progress is null)
        {
            return;
        }

        long entries;
        long logical;
        long represented;
        ScanBackend? activeBackend;
        ScanBackend? fallbackFrom;
        string? fallbackReason;
        bool mftActive;
        long mftScanned = 0;
        long mftTotal = 0;
        int mftPass = -1;

        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!force && now < _nextReportAt)
            {
                return;
            }

            _nextReportAt = now + (long)_options.ProgressInterval.TotalMilliseconds;
            entries = _ids is null ? _entries.Count : checked((int)Math.Min(int.MaxValue, _files + _directories));
            logical = _logicalBytes;
            represented = _representedBytes;
            activeBackend = _activeBackend;
            fallbackFrom = _fallbackFrom;
            fallbackReason = _fallbackReason;
            mftActive = _mftTotalBytes.HasValue && _mftPassIndex >= 0;
            if (mftActive)
            {
                mftScanned = _mftBytesScanned;
                mftTotal = _mftTotalBytes ?? 0;
                mftPass = _mftPassIndex;
            }
        }

        // Computed outside the gate from the captured count and the cached used
        // total, so no tick touches capacity or the disk.
        int? percent;
        if (phase == ScanPhase.Finished)
        {
            percent = 100;
        }
        else if (mftActive && (phase == ScanPhase.Building || phase == ScanPhase.Publishing))
        {
            // Traversal is done; hold below completion until the accepted finish.
            percent = 99;
        }
        else if (mftActive && (phase == ScanPhase.IndexingFolders || phase == ScanPhase.Enumerating))
        {
            // A direct MFT-phase tick carries its own traversal percent via ReportMft;
            // this generic path is only a fallback and must not jump back to the
            // portable estimate. Hold the last native position instead.
            if (mftTotal <= 0 || mftPass < 0)
            {
                percent = 0;
            }
            else
            {
                double done = Math.Clamp((double)mftScanned / (double)mftTotal, 0, 1);
                percent = Math.Clamp((int)(((mftPass + done) / MftPassCount) * 100.0), 0, 99);
            }
        }
        else if (mftActive)
        {
            percent = 99;
        }
        else
        {
            long? used = UsedBytesForProgress;
            if (used is null)
            {
                percent = null;
            }
            else if (used <= 0)
            {
                percent = 0;
            }
            else
            {
                double ratio = (double)represented * 100.0 / (double)used.Value;
                if (double.IsNaN(ratio) || ratio < 0)
                {
                    percent = 0;
                }
                else if (ratio >= 100)
                {
                    percent = 99;
                }
                else
                {
                    percent = (int)ratio;
                }
            }
        }

        ScanProgress tick = new(phase, currentPath, entries, logical)
        {
            PercentComplete = percent,
            ActiveBackend = activeBackend,
            FallbackFrom = fallbackFrom,
            FallbackReason = fallbackReason,
        };

        progress.Report(tick);
    }

    /// <summary>
    /// One consistent picture for a preview build: the entries copied under the gate
    /// together with the represented total for exactly those entries, so a preview never
    /// mixes a newer total with an older entry list. Legacy path for small trees and tests;
    /// production previews freeze the tree directly via <see cref="CaptureTreeSnapshot"/>.
    /// </summary>
    public (ScanEntry[] Entries, long RepresentedBytes) CapturePreview()
    {
        lock (_gate)
        {
            return (_ids is null ? _entries.ToArray() : [.. _tree.EnumerateEntries()], _representedBytes);
        }
    }

    /// <summary>
    /// Consistent tree picture for a preview or final build: folder structure shallow-copied
    /// under the gate with the represented total for exactly those entries. File nodes are
    /// shared references, never cloned. Each capture still allocates one reference array
    /// per folder plus the frozen child arrays of the built immutable nodes (O(files)
    /// references transient per preview); old and current published snapshots overlap.
    /// </summary>
    public (ManagedScanTree.FolderSnapshot Tree, long RepresentedBytes) CaptureTreeSnapshot()
    {
        lock (_gate)
        {
            return (ManagedScanTree.FolderSnapshot.Copy(_tree.Root), _representedBytes);
        }
    }

    /// <summary>
    /// Builds the immutable tree from a captured snapshot outside the gate. Folder nodes are
    /// new per capture; file nodes are the shared instances created at add time.
    /// </summary>
    public static TreemapNode BuildFromSnapshot(
        string rootPath, ManagedScanTree.FolderSnapshot snap, CancellationToken cancellationToken)
        => ManagedScanTree.BuildRoot(rootPath, snap, cancellationToken);

    /// <summary>
    /// How many entries are held, without copying them. The preview loop reads this
    /// first so an unchanged entry set costs no tree copy.
    /// </summary>
    public int EntryCount
    {
        get
        {
            lock (_gate)
            {
                return _ids is null
                    ? _entries.Count
                    : checked((int)Math.Min(int.MaxValue, _files + _directories));
            }
        }
    }

    /// <summary>Files plus explicitly recorded folders, without copying. Preview change detection.</summary>
    public long TotalEntryCount
    {
        get
        {
            lock (_gate)
            {
                return _files + _directories;
            }
        }
    }

    /// <summary>
    /// Final-only destructive freeze: builds the immutable root directly from the
    /// mutable working tree while releasing dictionaries as each folder is copied.
    /// Avoids the transient full <see cref="ManagedScanTree.FolderSnapshot"/> copy the
    /// preview path uses. Shared file nodes move by reference; counters, diagnostics
    /// and pending ids are untouched. The working tree is empty afterwards. Call once
    /// on the scanning thread after backends joined and previews drained.
    /// A final-build OutOfMemoryException propagates to the caller; it is never
    /// treated as a paused preview.
    /// </summary>
    public TreemapNode BuildFinalTreeDestructive(string rootPath, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _tree.BuildFinalDestructive(rootPath, cancellationToken);
        }
    }

    /// <summary>Drops everything a failed backend collected, so a retry starts clean.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _tree.Clear();
            _entries.Clear();
            _entries.TrimExcess();
            _diagnostics.Clear();
            _diagnostics.TrimExcess();
            _files = 0;
            _directories = 0;
            _logicalBytes = 0;
            _physicalBytes = 0;
            _representedBytes = 0;
            _hardLinkedFiles = 0;
            _suppressedDiagnostics = 0;
            _budgetUsed = 0;
            _estimatedMemoryBytes = 0;
            _memoryLimitReached = false;
            _incompleteReason = null;
            _allocationUnavailable = false;
            _complete = true;
            ClearMftLocked();
        }
    }

    private void ClearMftLocked()
    {
        _mftTotalBytes = null;
        _mftPassIndex = -1;
        _mftBytesScanned = 0;
    }

    /// <summary>Claims one unit of the shared budget. Caller holds the gate.</summary>
    private bool ClaimLocked(long memoryCost)
    {
        if (_memoryLimitReached)
        {
            return false;
        }

        if (_budgetUsed >= _options.MaxEntries)
        {
            MarkIncompleteLocked($"Stopped after {_options.MaxEntries} entries, the configured budget.");
            return false;
        }

        if (!ReserveMemoryLocked(memoryCost))
        {
            return false;
        }

        _budgetUsed++;
        return true;
    }

    /// <summary>Charges backend workspace, including the NTFS directory map.</summary>
    public bool TryReserveMemory(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (_gate)
        {
            return ReserveMemoryLocked(bytes);
        }
    }

    private bool ReserveMemoryLocked(long bytes)
    {
        if (_memoryLimitReached || bytes > _memoryLimit - _estimatedMemoryBytes)
        {
            MarkMemoryLimitLocked();
            return false;
        }

        _estimatedMemoryBytes += bytes;
        return true;
    }

    private void MarkMemoryLimitLocked()
    {
        _memoryLimitReached = true;
        _complete = false;
        // Resource truncation must remain visible even after an access diagnostic.
        long held = _ids is null ? _entries.Count : _files + _directories;
        _incompleteReason = $"Stopped after {held:N0} entries to stay within the scan memory budget. Scan a smaller folder for more detail";
    }

    private void MarkIncompleteLocked(string reason)
    {
        _complete = false;
        _incompleteReason ??= reason;
    }

    /// <summary>Sums without wrapping: a total that has silently gone negative is worse than one that saturates.</summary>
    private static long SaturatingAdd(long running, long value)
    {
        long add = value > 0 ? value : 0;
        return running > long.MaxValue - add ? long.MaxValue : running + add;
    }
}
