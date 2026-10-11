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

    private readonly List<ScanEntry> _entries = [];
    private readonly List<ScanDiagnostic> _diagnostics = [];

    private long _files;
    private long _directories;
    private long _logicalBytes;
    private long _physicalBytes;
    private long _representedBytes;
    private long _hardLinkedFiles;
    private long _suppressedDiagnostics;
    private long _budgetUsed;
    private long _nextReportAt;
    private string? _incompleteReason;
    private bool _allocationUnavailable;
    private bool _complete = true;

    public ScanCollector(ScanOptions options, IProgress<ScanProgress>? progress)
    {
        _options = options;
        _progress = progress;
    }

    public IReadOnlyList<ScanEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
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
    public bool TryAdd(
        string relativePath,
        NodeKind kind,
        long logicalSize,
        long physicalSize,
        DateTimeOffset? lastWriteTime,
        bool isExtraHardLinkEntry = false)
    {
        lock (_gate)
        {
            if (!ClaimLocked())
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

        if (!ClaimLocked() || _diagnostics.Count >= _options.MaxDiagnostics)
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

        ScanProgress tick;

        lock (_gate)
        {
            long now = _clock.ElapsedMilliseconds;
            if (!force && now < _nextReportAt)
            {
                return;
            }

            _nextReportAt = now + (long)_options.ProgressInterval.TotalMilliseconds;
            tick = new ScanProgress(phase, currentPath, _entries.Count, _logicalBytes);
        }

        progress.Report(tick);
    }

    /// <summary>
    /// One consistent picture for a preview build: the entries copied under the gate
    /// together with the represented total for exactly those entries, so a preview never
    /// mixes a newer total with an older entry list.
    /// </summary>
    public (ScanEntry[] Entries, long RepresentedBytes) CapturePreview()
    {
        lock (_gate)
        {
            return (_entries.ToArray(), _representedBytes);
        }
    }

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
                return _entries.Count;
            }
        }
    }

    /// <summary>Drops everything a failed backend collected, so a retry starts clean.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
            _diagnostics.Clear();
            _files = 0;
            _directories = 0;
            _logicalBytes = 0;
            _physicalBytes = 0;
            _representedBytes = 0;
            _hardLinkedFiles = 0;
            _suppressedDiagnostics = 0;
            _budgetUsed = 0;
            _incompleteReason = null;
            _allocationUnavailable = false;
            _complete = true;
        }
    }

    /// <summary>Claims one unit of the shared budget. Caller holds the gate.</summary>
    private bool ClaimLocked()
    {
        if (_budgetUsed >= _options.MaxEntries)
        {
            MarkIncompleteLocked($"Stopped after {_options.MaxEntries} entries, the configured budget.");
            return false;
        }

        _budgetUsed++;
        return true;
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
