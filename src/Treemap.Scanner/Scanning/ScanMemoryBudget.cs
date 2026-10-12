namespace Treemap.Scanning;

/// <summary>Estimated scan storage and construction allowance, supplementing external process limits.</summary>
/// <remarks>All numbers are estimates, not a hard heap cap: the OS job cap and GC heap still govern.</remarks>
internal static class ScanMemoryBudget
{
    /// <summary>Caps the automatic estimate even when the machine has much more available memory.</summary>
    public const long MaxAutomaticBytes = 1_300_000_000L;

    public static long ResolveLimit(ScanOptions options)
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long remaining = Math.Max(0, available - GC.GetTotalMemory(false));
        // Reserve managed headroom for overlapping snapshots and construction.
        // The estimate is measured once; external process and heap limits remain
        // necessary because collection timing and native allocations vary.
        long automatic = Math.Min(remaining * 85 / 100, MaxAutomaticBytes);
        return options.MemoryBudgetBytes is { } configured ? Math.Min(configured, automatic) : automatic;
    }

    /// <summary>
    /// Retained cost of one file: single shared immutable node (~80), basename
    /// string overhead (~24 plus 2 per char below), one sibling-dictionary entry
    /// (~32), one id-map entry (~40) and one snapshot-index entry (~32), plus
    /// alignment/resize slack. Ancestor folders are charged once when created,
    /// never again per sibling. A rescan peak holds the committed map plus the
    /// growing pending map and old plus current snapshots; previews add transient
    /// reference arrays per folder plus frozen child arrays (see FileSystemScanner).
    /// </summary>
    public static long EstimateFile(int nameLength) => 256L + (2L * nameLength);

    /// <summary>
    /// Retained cost of one newly created folder: mutable folder (~64) plus its
    /// final immutable node (~80), basename overhead (~24 plus 2 per char), one
    /// parent-dictionary entry (~32), one id-map entry (~40), one snapshot-index
    /// entry (~32) and amortised child-dictionary objects (~80), plus resize
    /// slack. Charged once per folder. Duplicate emissions still claim a budget
    /// unit and count toward MaxEntries (legacy per-emission semantics) but
    /// allocate no second folder. Folder entries also carry transient
    /// absolute-path workspace (2 per root char) charged by the collector.
    /// </summary>
    public static long EstimateFolder(int nameLength) => 512L + (2L * nameLength);

    public static long EstimateEntry(string path)
    {
        // Legacy entry point: honest per-entry cost without the old per-sibling ancestor
        // recharge. New code charges files and actually-new folders separately.
        return EstimateFile(path.Length);
    }

    public static long EstimateDiagnostic(string path, string message)
        => 256L + (2L * path.Length) + (2L * message.Length);

    /// <summary>
    /// Preview timer delay for the current entry count. Large trees slow previews
    /// so one O(N) capture can finish and older views can collect before the next:
    /// at least 2s from 1M entries, 3s from 2M. A larger caller interval is always
    /// honoured; small trees keep the caller's interval untouched.
    /// </summary>
    internal static TimeSpan PreviewDelay(TimeSpan requested, long entryCount)
    {
        TimeSpan floor = entryCount >= 2_000_000 ? TimeSpan.FromSeconds(3)
            : entryCount >= 1_000_000 ? TimeSpan.FromSeconds(2)
            : TimeSpan.Zero;
        return floor > requested ? floor : requested;
    }
}
