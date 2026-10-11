namespace Treemap.Scanning;

/// <summary>
/// What one scanner instance learned about one filesystem entry, in the form the shared
/// tree builder consumes. Both backends produce these and nothing else.
/// </summary>
/// <param name="RelativePath">Path below the scan root, separated by <c>\</c>.</param>
/// <param name="Kind">Folder or file.</param>
/// <param name="LogicalSize">Apparent size in bytes.</param>
/// <param name="PhysicalSize">Allocated size in bytes, or 0 when unknown.</param>
/// <param name="LastWriteTime">Last write time, or <see langword="null"/>.</param>
internal readonly record struct ScanEntry(
    string RelativePath,
    NodeKind Kind,
    long LogicalSize,
    long PhysicalSize,
    DateTimeOffset? LastWriteTime);