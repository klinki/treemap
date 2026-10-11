namespace Treemap.Scanning;

/// <summary>Immutable input handed to a backend for one attempt.</summary>
internal readonly record struct ScanRequest(
    string RootPath,
    ScanOptions Options,
    CancellationToken CancellationToken);

/// <summary>
/// A scanner that fills a <see cref="ScanCollector"/>.
/// </summary>
/// <remarks>
/// A backend returns <see langword="false"/> only when it cannot produce a trustworthy
/// result at all - the volume could not be opened, or the records it read did not validate.
/// The caller then discards everything collected and restarts with the portable backend.
/// Failures confined to individual subtrees are not a reason to decline: they become
/// diagnostics and the result is marked incomplete.
/// </remarks>
internal interface IScanBackend
{
    ScanBackend Id { get; }

    bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason);
}

/// <summary>Exceptions that describe "this path is unreadable", not "this program is broken".</summary>
internal static class ScanFailure
{
    /// <summary>
    /// True for the exceptions a per-entry handler should record and move past. Anything
    /// outside this list - <see cref="OutOfMemoryException"/>,
    /// <see cref="OperationCanceledException"/>, <see cref="StackOverflowException"/> -
    /// keeps propagating instead of being laundered into a diagnostic or a fallback.
    /// </summary>
    public static bool IsRecoverable(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    public static string Describe(Exception exception)
        => exception.Message.ReplaceLineEndings(" ");
}