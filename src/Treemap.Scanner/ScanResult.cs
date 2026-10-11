namespace Treemap.Scanning;

/// <summary>The outcome of one scan: the tree, where it came from, what went missing, and why.</summary>
/// <remarks>
/// <para>
/// No result is returned when there is none to give: the root is missing, is not a
/// directory, or cannot be read (these throw), or the caller cancelled (which throws
/// <see cref="OperationCanceledException"/>). Everything short of that produces a result,
/// complete or not - an unreadable subtree inside a readable root is a fact to display.
/// </para>
///
/// <para>
/// <b>Sizes.</b> <see cref="TotalLogicalSize"/> sums apparent file lengths,
/// <see cref="TotalPhysicalSize"/> sums allocated sizes. Both are file totals: the
/// synthetic free-space and unaccounted-space blocks report through
/// <see cref="FreeSpaceBytes"/> and <see cref="UnknownSpaceBytes"/> instead, so
/// toggling their display never changes what the scan measured.
/// <see cref="TotalPhysicalSize"/> is 0 where allocation is unavailable;
/// <see cref="TreemapNode.LayoutSize"/> falls back to logical size for zero
/// allocation, including resident or fully sparse files.
/// </para>
///
/// <para>
/// <b>Hard links.</b> A hard-linked file appears under every directory it is linked into,
/// each name getting its own node and its own contribution - which is what the portable
/// backend does too, so a fallback does not change the shape of the tree. The consequence
/// is deliberate: <b>totals count a hard-linked file once per link</b>.
/// <see cref="HardLinkedFileCount"/> reports the extra nodes so a host can warn about it.
/// </para>
/// </remarks>
public sealed record ScanResult
{
    /// <summary>Absolute, normalized path that was scanned.</summary>
    public required string RootPath { get; init; }

    /// <summary>The tree. Node ids are stable per scanner instance.</summary>
    public required TreemapNode Root { get; init; }

    /// <summary>
    /// The snapshot handed to the caller, when one was published. Published from the
    /// scanning thread, never from the caller's.
    /// </summary>
    public TreemapSnapshot? Snapshot { get; init; }

    /// <summary>The backend whose data ended up in <see cref="Root"/>.</summary>
    public required ScanBackend Backend { get; init; }

    /// <summary>The backend that was tried and abandoned, or null when none was.</summary>
    public ScanBackend? FallbackFrom { get; init; }

    /// <summary>Why <see cref="FallbackFrom"/> was abandoned. Retained even when the fallback worked.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// False when the scan could not read something it should have: an unreadable subtree,
    /// a skipped entry, or a limit it stopped at. A deliberate policy that cost detail - a
    /// link that was not followed - is a <see cref="ScanDiagnosticSeverity.Warning"/> and
    /// does not clear this.
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>Short summary of why the result is incomplete.</summary>
    public string? IncompleteReason { get; init; }

    /// <summary>Number of file nodes in the tree.</summary>
    public long FileCount { get; init; }

    /// <summary>Number of directory nodes, excluding the root.</summary>
    public long DirectoryCount { get; init; }

    /// <summary>Sum of apparent file lengths across all nodes, hard links included.</summary>
    public long TotalLogicalSize { get; init; }

    /// <summary>Sum of allocated sizes across all nodes, or 0 when allocation is unavailable.</summary>
    public long TotalPhysicalSize { get; init; }

    /// <summary>Free-space node added for a volume-root scan, or 0.</summary>
    /// <remarks>
    /// A point-in-time reading, attached to complete and incomplete volume scans alike.
    /// Never file content: it joins the root's physical total only, while
    /// <see cref="TotalLogicalSize"/> and <see cref="TotalPhysicalSize"/> stay file-only.
    /// </remarks>
    public long FreeSpaceBytes { get; init; }

    /// <summary>Unaccounted-space node added for a volume-root scan, or 0.</summary>
    /// <remarks>
    /// A safe nonnegative residual: used volume bytes minus the represented file
    /// allocation (falling back to apparent size where allocation is unknown),
    /// clamped at zero when duplicated hard links overcount. It covers metadata and
    /// allocation differences as well as files the scan could not read, so it is
    /// reported as "Unaccounted space" rather than as inaccessible files. Like
    /// <see cref="FreeSpaceBytes"/> it joins the root's physical total only and leaves
    /// the file totals alone.
    /// </remarks>
    public long UnknownSpaceBytes { get; init; }

    /// <summary>
    /// Extra nodes produced by hard links: a file linked into N directories contributes N
    /// nodes and this is N - 1. 0 when the backend cannot see link counts, which is the
    /// case for the portable backend on every platform.
    /// </summary>
    public long HardLinkedFileCount { get; init; }

    /// <summary>Bounded list of what went wrong, oldest first.</summary>
    public IReadOnlyList<ScanDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Problems observed but not retained in <see cref="Diagnostics"/>, because the
    /// diagnostic list or the shared entry budget filled up.
    /// </summary>
    public int SuppressedDiagnostics { get; init; }

    /// <summary>Wall-clock time of the scan, including the publish.</summary>
    public TimeSpan Duration { get; init; }
}
