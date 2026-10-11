namespace Treemap.Scanning;

/// <summary>Conservative workspace estimates, separate from the external process cap.</summary>
internal static class ScanMemoryBudget
{
    public static long ResolveLimit(ScanOptions options)
    {
        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long remaining = Math.Max(0, available - GC.GetTotalMemory(false));
        // Leave half the remaining heap for construction, retained UI snapshots, and
        // allocations outside the scanner. Measure only once, before discovery.
        long automatic = remaining / 2;
        return options.MemoryBudgetBytes is { } configured ? Math.Min(configured, automatic) : automatic;
    }

    public static long EstimateEntry(string path)
    {
        // Entry, names, mutable/immutable nodes, ID maps, and snapshot indexes.
        long estimate = 1024L + (4L * path.Length);
        bool hasSegment = false;
        for (int i = 0; i < path.Length; i++)
        {
            if (path[i] == Path.DirectorySeparatorChar || path[i] == Path.AltDirectorySeparatorChar)
            {
                if (hasSegment)
                {
                    // A backend may omit parent entries. Include their nodes and
                    // below-root prefix strings without retaining another path index.
                    estimate += 512L + (2L * i);
                    hasSegment = false;
                }
            }
            else
            {
                hasSegment = true;
            }
        }

        return estimate;
    }

    public static long EstimateDiagnostic(string path, string message)
        => 256L + (2L * path.Length) + (2L * message.Length);
}
