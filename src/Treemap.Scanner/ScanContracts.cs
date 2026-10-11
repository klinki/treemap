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
    /// Discovered-storage estimate for whole-volume scans, 0 to 99 until
    /// <see cref="ScanPhase.Finished"/> reports 100. Null means unknown: folder scans
    /// or unavailable capacity. A zero used-byte total reports 0, never null.
    /// </summary>
    public int? PercentComplete { get; init; }
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