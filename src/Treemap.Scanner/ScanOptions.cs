namespace Treemap.Scanning;

/// <summary>
/// Knobs for one scan. Every field has a bounded default: the scanner is pointed at a
/// real filesystem by a long-running desktop process, so an accidental unbounded walk is
/// a memory incident, not a slow query.
/// </summary>
/// <remarks>
/// Instances are immutable. <see cref="FileSystemScanner.ScanAsync"/> validates the values
/// before any I/O and throws <see cref="ArgumentOutOfRangeException"/> rather than
/// silently clamping them.
/// </remarks>
public sealed record ScanOptions
{
    /// <summary>Highest number of directory enumerations the portable backend runs at once.</summary>
    public const int MaxParallelismHardLimit = 4;

    /// <summary>Deepest level a scan may reach. Bounded because <see cref="TreemapSnapshot"/> indexes its tree recursively.</summary>
    public const int MaxDepthHardLimit = 256;

    /// <summary>Default shared budget for entries and diagnostics. See <see cref="MaxEntries"/>.</summary>
    public const long DefaultMaxEntries = 500_000;

    /// <summary>Default cap on retained <see cref="ScanDiagnostic"/> values.</summary>
    public const int DefaultMaxDiagnostics = 200;

    /// <summary>The options used when the caller passes none.</summary>
    public static ScanOptions Default { get; } = new();

    /// <summary>
    /// Which backend to try first. Defaults to <see cref="ScanBackendPreference.Auto"/>:
    /// the NTFS MFT reader on Windows, the portable scanner everywhere else.
    /// </summary>
    public ScanBackendPreference Backend { get; init; } = ScanBackendPreference.Auto;

    /// <summary>
    /// Concurrent directory enumerations in the portable backend, from 1 to
    /// <see cref="MaxParallelismHardLimit"/>. Defaults to 2: mechanical storage prefers few
    /// streams, and the work is IO-bound rather than CPU-bound.
    /// </summary>
    public int MaxParallelism { get; init; } = 2;

    /// <summary>
    /// Deepest directory level to descend into, where the scan root itself is 0. Must be
    /// between 1 and <see cref="MaxDepthHardLimit"/>. Hitting it yields an explicitly
    /// incomplete result, never a silent truncation.
    /// </summary>
    public int MaxDepth { get; init; } = MaxDepthHardLimit;

    /// <summary>
    /// Shared budget for everything the scan discovers: files, directories and recorded
    /// diagnostics all draw from it. Must be at least 1. This is an entry budget, not a
    /// measured heap limit; the default simply keeps a walk bounded by count, and a host
    /// scanning a large volume should raise it deliberately. Reaching the budget is
    /// reported through <see cref="ScanResult.IsComplete"/>.
    /// </summary>
    public long MaxEntries { get; init; } = DefaultMaxEntries;

    /// <summary>
    /// How many diagnostics to retain. Further problems keep counting against
    /// <see cref="MaxEntries"/> and are summarised by
    /// <see cref="ScanResult.SuppressedDiagnostics"/>, so a scan cannot hide its failures
    /// by generating many of them.
    /// </summary>
    public int MaxDiagnostics { get; init; } = DefaultMaxDiagnostics;

    /// <summary>
    /// Attributes to skip. Defaults to <see cref="FileAttributes.None"/>, so hidden and
    /// system files are scanned like everything else. Skipping is only ever explicit.
    /// </summary>
    /// <remarks>Not validated as an enum: unknown bits simply never match a real attribute.</remarks>
    public FileAttributes SkipAttributes { get; init; } = FileAttributes.None;

    /// <summary>
    /// Ask for allocated ("on disk") size in addition to the file's apparent length. Costs
    /// one extra syscall per file where it is available and reports
    /// <see cref="ScanResult.TotalPhysicalSize"/> honestly.
    /// </summary>
    public bool QueryAllocatedSize { get; init; } = true;

    /// <summary>
    /// Add a free-space block to a volume-root scan. Ignored for any other root: a
    /// folder's own treemap must never be padded with the whole disk's free bytes.
    /// </summary>
    public bool IncludeFreeSpace { get; init; } = true;

    /// <summary>
    /// Add an "Unaccounted space" block to a volume-root scan, like
    /// <see cref="IncludeFreeSpace"/> but for the other side of the ledger: the used
    /// bytes no scanned file accounts for. The residual also covers metadata and
    /// allocation differences, not just files the scan could not read, which is why it
    /// is not called inaccessible space. Ignored for any other root.
    /// </summary>
    public bool IncludeUnknownSpace { get; init; } = true;

    /// <summary>
    /// Publish work-in-progress trees to the publisher while the scan is still running.
    /// Defaults to false: a library caller gets exactly one publish, the finished tree.
    /// The desktop host opts in for its live map.
    /// </summary>
    public bool PublishIntermediateSnapshots { get; init; }

    /// <summary>
    /// Minimum gap between intermediate snapshot builds. Separate from
    /// <see cref="ProgressInterval"/>: progress ticks are cheap records, previews rebuild
    /// the tree. Below 10 ms the timer is still armed with a 10 ms delay, so a zero or
    /// submillisecond interval cannot spin a pool thread; tests use zero on tiny
    /// backends only, and hosts should not. Above <see cref="MaxSnapshotInterval"/> the
    /// value is rejected, because <see cref="Timer"/> cannot arm it.
    /// </summary>
    public TimeSpan SnapshotInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Largest gap the preview timer supports: the <see cref="Timer"/> due-time bound.</summary>
    public static readonly TimeSpan MaxSnapshotInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// Minimum gap between progress reports. <see cref="TimeSpan.Zero"/> reports on every
    /// entry, which tests use and hosts should not.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Rejects values outside their documented ranges.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range.</exception>
    public ScanOptions Validate()
    {
        // An undefined preference would otherwise read as "not Portable" and quietly
        // select a backend the caller never asked for.
        if (Backend is not (ScanBackendPreference.Auto or ScanBackendPreference.Portable or ScanBackendPreference.NtfsMft))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Backend), Backend, "Must be Auto, Portable or NtfsMft.");
        }

        if (MaxParallelism is < 1 or > MaxParallelismHardLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxParallelism), MaxParallelism, $"Must be between 1 and {MaxParallelismHardLimit}.");
        }

        if (MaxDepth is < 1 or > MaxDepthHardLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxDepth), MaxDepth, $"Must be between 1 and {MaxDepthHardLimit}.");
        }

        if (MaxEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), MaxEntries, "Must be at least 1.");
        }

        if (MaxDiagnostics < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDiagnostics), MaxDiagnostics, "Must not be negative.");
        }

        if (ProgressInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ProgressInterval), ProgressInterval, "Must not be negative.");
        }

        if (SnapshotInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SnapshotInterval), SnapshotInterval, "Must not be negative.");
        }

        if (SnapshotInterval > MaxSnapshotInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SnapshotInterval), SnapshotInterval, $"Must not exceed {MaxSnapshotInterval}.");
        }

        return this;
    }
}