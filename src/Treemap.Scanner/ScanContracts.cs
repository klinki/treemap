namespace Treemap.Scanning;

/// <summary>The scanner that actually produced a result.</summary>
public enum ScanBackend
{
    /// <summary>Streaming <see cref="DirectoryInfo"/> enumeration. Available on every platform.</summary>
    Portable,

    /// <summary>
    /// Windows NTFS Master File Table read through a read-only volume handle. Falls back to
    /// <see cref="Portable"/> when the volume cannot be opened or its records cannot be
    /// trusted.
    /// </summary>
    NtfsMft,
}

/// <summary>
/// What the caller asked for. <see cref="Auto"/> prefers the fast backend; the other two
/// are honoured as written. A request that cannot be satisfied still degrades, with the
/// reason recorded on <see cref="ScanResult.FallbackReason"/>.
/// </summary>
public enum ScanBackendPreference
{
    Auto,
    Portable,
    NtfsMft,
}

/// <summary>Coarse stage of a scan, carried on <see cref="ScanProgress"/>.</summary>
public enum ScanPhase
{
    Starting,
    Enumerating,
    Building,
    Publishing,
    Finished,
    /// <summary>
    /// First NTFS MFT pass: indexing folders. The second pass keeps <see cref="Enumerating"/>.
    /// Appended so existing numeric values never change.
    /// </summary>
    IndexingFolders,
}

/// <summary>
/// How much a diagnostic should weigh. Only <see cref="Error"/> clears
/// <see cref="ScanResult.IsComplete"/>: an error means the scan could not read something it
/// should have, while a warning records a deliberate policy that cost some detail.
/// </summary>
public enum ScanDiagnosticSeverity
{
    /// <summary>Something was deliberately left out. Reported, but not a failure.</summary>
    Warning,

    /// <summary>A subtree or an entry could not be read at all.</summary>
    Error,
}

/// <summary>
/// A progress tick. Reported from a worker thread, so a UI host must marshal it before
/// touching controls.
/// </summary>
public readonly record struct ScanProgress(
    ScanPhase Phase,
    string? CurrentPath,
    long EntriesScanned,
    long BytesScanned)
{
    /// <summary>
    /// Work progress, 0 to 99 until <see cref="ScanPhase.Finished"/> reports 100.
    /// Native scans report known MFT table traversal across both passes for any root;
    /// portable scans report a discovered-storage estimate for whole-volume scans.
    /// Null means unknown: portable folder scans or unavailable capacity. A zero
    /// used-byte total reports 0, never null.
    /// </summary>
    public int? PercentComplete { get; init; }

    /// <summary>
    /// The backend currently doing the work, once one has been selected. Null only
    /// before selection: the scan starts choosing, then reports NTFS or portable as
    /// soon as the attempt begins, so a host never sits on "choosing" for a whole walk.
    /// </summary>
    public ScanBackend? ActiveBackend { get; init; }

    /// <summary>
    /// The backend that was tried and abandoned for the portable retry, once a
    /// fallback has happened. Null when no backend has been abandoned.
    /// </summary>
    public ScanBackend? FallbackFrom { get; init; }

    /// <summary>Why <see cref="FallbackFrom"/> was abandoned, once it has been.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// Traversal position through the current MFT pass's table span, in bytes. Null when the
    /// tick is not a native table walk (portable scans, building, publishing, finished).
    /// The span covers vacant skipped slots too, so it is traversal work rather than I/O
    /// bytes or emitted file bytes.
    /// </summary>
    public long? MftBytesScanned { get; init; }

    /// <summary>
    /// Known MFT table span for the current pass, in bytes (<c>RecordCount * RecordSize</c>).
    /// Null when unknown or not a native table walk.
    /// </summary>
    public long? MftTotalBytes { get; init; }

    /// <summary>
    /// Approximate remaining time for the current pass only, not the whole scan. Null until
    /// enough of the pass has been observed, and never set for finalization phases.
    /// </summary>
    public TimeSpan? EstimatedRemainingTime { get; init; }
}

/// <summary>One bounded, human-readable note about something the scan could not do exactly.</summary>
/// <param name="Severity">How much the note matters.</param>
/// <param name="Path">The path the note is about, or an empty string.</param>
/// <param name="Message">What happened.</param>
public sealed record ScanDiagnostic(ScanDiagnosticSeverity Severity, string Path, string Message);

/// <summary>A mounted volume that is ready to be scanned.</summary>
/// <param name="Name">Platform volume name, for example <c>C:\</c>.</param>
/// <param name="RootDirectory">Full path to scan.</param>
/// <param name="DriveType">Reported by <see cref="DriveInfo.DriveType"/>.</param>
/// <param name="Format">Reported by <see cref="DriveInfo.DriveFormat"/>, for example <c>NTFS</c>.</param>
/// <param name="TotalSize">Total bytes on the volume.</param>
/// <param name="AvailableFreeSpace">Free bytes on the volume at the moment of discovery.</param>
public sealed record ScannableVolume(
    string Name,
    string RootDirectory,
    DriveType DriveType,
    string Format,
    long TotalSize,
    long AvailableFreeSpace);