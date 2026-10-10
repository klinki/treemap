namespace Treemap;

/// <summary>
/// An immutable, atomically-published view of the model at a point in time.
/// </summary>
/// <remarks>
/// <para>
/// This is the hinge of the whole concurrency design. A snapshot is constructed
/// on a worker thread while the scanner holds its write lock, then published with a
/// single <c>Volatile.Write</c> / <c>Volatile.Read</c> pair. The render thread never
/// takes a lock: it grabs a reference and walks it freely.
/// </para>
///
/// <para>
/// Because instances are immutable and self-consistent, the renderer can hold one
/// across an entire frame — and the animator can hold <em>two</em> (previous and
/// next) simultaneously to interpolate between them. That is the capability a
/// reader/writer lock cannot provide, and the reason this design exists.
/// </para>
/// </remarks>
public sealed class TreemapSnapshot
{
    private readonly Dictionary<int, TreemapNode> _nodesById;

    public TreemapSnapshot(TreemapNode root, long version, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(root);

        Root = root;
        Version = version;
        Timestamp = timestamp;

        Dictionary<int, TreemapNode> map = new(256);
        Index(map, root);
        _nodesById = map;
    }

    public TreemapNode Root { get; }

    /// <summary>Monotonic counter, incremented on each publish.</summary>
    public long Version { get; }

    public DateTimeOffset Timestamp { get; }

    public int NodeCount => _nodesById.Count;

    public bool TryGetNode(int id, out TreemapNode node) => _nodesById.TryGetValue(id, out node!);

    /// <summary>Builds a snapshot with a fresh version number.</summary>
    public static TreemapSnapshot Create(TreemapNode root, long version)
        => new(root, version, DateTimeOffset.UtcNow);

    private static void Index(Dictionary<int, TreemapNode> map, TreemapNode node)
    {
        map[node.Id] = node;

        foreach (TreemapNode child in node.Children)
        {
            Index(map, child);
        }
    }
}

/// <summary>
/// Publishes <see cref="TreemapSnapshot"/> instances to the render thread.
///
/// <para>
/// Mutations are serialised by an internal lock so a scanner can keep the working
/// tree mutable, while readers stay completely lock-free.
/// </para>
/// </summary>
public sealed class SnapshotPublisher
{
    // System.Threading.Lock (.NET 9+) is allocation-free and faster than Monitor.
    private readonly Lock _publishLock = new();
    private TreemapSnapshot _current;
    private long _version;

    public SnapshotPublisher(TreemapNode emptyRoot)
    {
        ArgumentNullException.ThrowIfNull(emptyRoot);

        _current = new TreemapSnapshot(emptyRoot, 0, DateTimeOffset.UtcNow);
    }

    /// <summary>The most recently published snapshot. Safe to call from any thread.</summary>
    public TreemapSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Publishes a new snapshot built from <paramref name="root"/>. Returns the
    /// published instance so the caller can hand it straight to an animator.
    /// </summary>
    public TreemapSnapshot Publish(TreemapNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        TreemapSnapshot snapshot;
        lock (_publishLock)
        {
            snapshot = new TreemapSnapshot(root, ++_version, DateTimeOffset.UtcNow);
        }

        // Release-store: everything written above is visible to any thread that
        // observes this reference via Volatile.Read.
        Volatile.Write(ref _current, snapshot);
        return snapshot;
    }
}
