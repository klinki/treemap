using Treemap;
using Treemap.Scanning;

namespace Treemap.Scanner.Tests;

/// <summary>
/// Stand-ins for a backend, so the orchestration around them can be exercised without a
/// volume handle. They drive the real <see cref="ScanCollector"/>: the point is to test the
/// seam the scanner actually offers, not a mock of it.
/// </summary>
internal sealed class ScriptedBackend : IScanBackend
{
    private readonly ScanBackend _id;

    public ScriptedBackend(ScanBackend id) => _id = id;

    public ScanBackend Id => _id;

    /// <summary>Entries to add before the backend decides what to do.</summary>
    public List<ScanEntry> Entries { get; } = [];

    /// <summary>Result of <see cref="TryRun"/>: true succeeds, false declines with a reason.</summary>
    public bool Succeeds { get; set; } = true;

    public string? FailureReason { get; set; }

    /// <summary>Thrown instead of returning, to model a backend that is simply broken.</summary>
    public Exception? Throw { get; set; }

    /// <summary>
    /// Runs after the token has been checked and before anything is collected, so a test
    /// can cancel mid-backend and still see what the backend decided afterwards.
    /// </summary>
    public Action? OnRun { get; set; }

    /// <summary>When true, additional names are marked as extra hard-link entries.</summary>
    public bool MarkExtraNamesAsHardLinks { get; set; }

    public int Runs { get; private set; }

    public bool TryRun(ScanRequest request, ScanCollector collector, out string? failureReason)
    {
        Runs++;
        request.CancellationToken.ThrowIfCancellationRequested();
        OnRun?.Invoke();

        if (Throw is not null)
        {
            throw Throw;
        }

        var hardLinked = new HashSet<string>(StringComparer.Ordinal);

        foreach (ScanEntry entry in Entries)
        {
            request.CancellationToken.ThrowIfCancellationRequested();

            // Entries whose leaf name repeats are how a hard-linked file reaches the
            // collector: one node per visible name, each charged its own size. The first
            // is the file; the rest are extra link entries.
            bool extra = MarkExtraNamesAsHardLinks && !hardLinked.Add(LeafName(entry.RelativePath));

            if (!collector.TryAdd(
                    entry.RelativePath,
                    entry.Kind,
                    entry.LogicalSize,
                    entry.PhysicalSize,
                    entry.LastWriteTime,
                    extra))
            {
                break;
            }
        }

        failureReason = Succeeds ? null : FailureReason ?? "the scripted backend declined";
        return Succeeds;
    }

    private static string LeafName(string relativePath)
    {
        int cut = relativePath.LastIndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        return cut < 0 ? relativePath : relativePath[(cut + 1)..];
    }
}

/// <summary>Cancels the scan from inside a progress callback.</summary>
internal sealed class CancellingProgress : IProgress<ScanProgress>
{
    private readonly CancellationTokenSource _source;
    private readonly long _afterEntries;

    public CancellingProgress(CancellationTokenSource source, long afterEntries)
    {
        _source = source;
        _afterEntries = afterEntries;
    }

    public int Reports { get; private set; }

    public void Report(ScanProgress value)
    {
        Reports++;

        if (value.EntriesScanned >= _afterEntries)
        {
            _source.Cancel();
        }
    }
}

/// <summary>Records every progress tick a scan emits.</summary>
internal sealed class RecordingProgress : IProgress<ScanProgress>
{
    private readonly List<ScanProgress> _ticks = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<ScanProgress> Ticks
    {
        get
        {
            lock (_gate)
            {
                return _ticks.ToArray();
            }
        }
    }

    public void Report(ScanProgress value)
    {
        lock (_gate)
        {
            _ticks.Add(value);
        }
    }
}

/// <summary>
/// A progress callback that fails the way a host's UI update can: an
/// <see cref="IOException"/>, which is the same shape as an unreadable directory. It
/// fails on the per-entry report, which is issued from inside a directory walk, because
/// that is the call a per-directory error handler would otherwise swallow.
/// </summary>
internal sealed class FailingProgress : IProgress<ScanProgress>
{
    public void Report(ScanProgress value)
    {
        if (value.Phase == ScanPhase.Enumerating && value.CurrentPath is not null)
        {
            throw new IOException("the host callback failed");
        }
    }
}

/// <summary>Options that report on every single entry, so tests need no timers.</summary>
internal static class TestOptions
{
    public static ScanOptions Verbose(ScanBackendPreference backend = ScanBackendPreference.Portable) => new()
    {
        Backend = backend,
        ProgressInterval = TimeSpan.Zero,
    };
}

/// <summary>An empty publisher root, matching the id the scanner gives a real root.</summary>
internal static class TestPublisher
{
    public static SnapshotPublisher Empty() => new(TreemapNode.Create(ScanIdMap.RootId, "empty", NodeKind.Drive));
}