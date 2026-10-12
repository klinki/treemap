namespace Treemap.Scanning;

/// <summary>Estimated scan storage and construction allowance, only when the caller opts in.</summary>
/// <remarks>No automatic heap-derived bound is applied: the default scan is unbounded and an
/// explicit <see cref="ScanOptions.MemoryBudgetBytes"/> is honoured exactly. External process
/// limits still govern.</remarks>
internal static class ScanMemoryBudget
{
    /// <summary>
    /// The scan's memory allowance in bytes: the caller's explicit budget exactly, or
    /// effectively unlimited (<c>long.MaxValue</c>) when none was given. There is no
    /// automatic estimate from the GC heap.
    /// </summary>
    public static long ResolveLimit(ScanOptions options)
        => options.MemoryBudgetBytes ?? long.MaxValue;

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
