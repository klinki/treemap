using System.Globalization;

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
    private readonly Dictionary<int, int> _parentById;

    public TreemapSnapshot(TreemapNode root, long version, DateTimeOffset timestamp, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        Root = root;
        Version = version;
        Timestamp = timestamp;
        RootPath = rootPath;

        // Pre-size both maps from a counting pass to avoid resize array overlap.
        int count = CountNodes(root);
        Dictionary<int, TreemapNode> map = new(count);
        Dictionary<int, int> parents = new(count);
        Index(map, parents, root, parentId: -1);
        _nodesById = map;
        _parentById = parents;
    }

    public TreemapNode Root { get; }

    /// <summary>Monotonic counter, incremented on each publish.</summary>
    public long Version { get; }

    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// Optional source the tree was scanned from (normalized root path).
    /// Null for legacy/synthetic callers. Lets the controller tell two different
    /// scans apart when they reuse the same node ids.
    /// </summary>
    public string? RootPath { get; }

    public int NodeCount => _nodesById.Count;

    public bool TryGetNode(int id, out TreemapNode node) => _nodesById.TryGetValue(id, out node!);

    /// <summary>
    /// The true parent of a node in the whole snapshot, indexed once per publish.
    /// Returns false for the root and for unknown ids.
    /// </summary>
    public bool TryGetParent(int id, out int parentId) => _parentById.TryGetValue(id, out parentId);

    /// <summary>
    /// Display path of a node, built from the snapshot alone with no filesystem I/O.
    /// With a <see cref="RootPath"/>, the root resolves to that absolute source and
    /// descendants append with the source's own separator style; otherwise segments
    /// are joined with '/' and the root comes first.
    /// </summary>
    public string GetPath(int id)
    {
        if (!_nodesById.TryGetValue(id, out TreemapNode? node))
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        List<string> segments = [node.Name];
        int current = id;
        while (_parentById.TryGetValue(current, out int parent)
            && _nodesById.TryGetValue(parent, out TreemapNode? parentNode))
        {
            segments.Add(parentNode.Name);
            current = parent;
        }

        segments.Reverse();

        if (RootPath is { Length: > 0 } source)
        {
            if (segments.Count <= 1)
            {
                return source;
            }

            char sep = source.Contains('\\') ? '\\'
                : source.Contains('/') ? '/'
                : Path.DirectorySeparatorChar;
            string trimmed = source.TrimEnd('\\', '/');
            return trimmed + sep + string.Join(sep, segments.Skip(1));
        }

        return string.Join("/", segments);
    }

    /// <summary>Builds a snapshot with a fresh version number.</summary>
    public static TreemapSnapshot Create(TreemapNode root, long version, string? rootPath = null)
        => new(root, version, DateTimeOffset.UtcNow, rootPath);

    /// <summary>
    /// Counts nodes so the lookup dictionaries can be pre-sized.
    /// Duplicate ids count once per visit: slight over-sizing is harmless.
    /// </summary>
    private static int CountNodes(TreemapNode node)
    {
        int count = 1;
        checked
        {
            IReadOnlyList<TreemapNode> children = node.Children;
            for (int i = 0; i < children.Count; i++)
            {
                count += CountNodes(children[i]);
            }
        }

        return count;
    }

    private static void Index(
        Dictionary<int, TreemapNode> map, Dictionary<int, int> parents, TreemapNode node, int parentId)
    {
        map[node.Id] = node;
        if (parentId >= 0)
        {
            parents[node.Id] = parentId;
        }

        foreach (TreemapNode child in node.Children)
        {
            Index(map, parents, child, node.Id);
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

    public SnapshotPublisher(TreemapNode emptyRoot, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(emptyRoot);

        _current = new TreemapSnapshot(emptyRoot, 0, DateTimeOffset.UtcNow, rootPath);
    }

    /// <summary>The most recently published snapshot. Safe to call from any thread.</summary>
    public TreemapSnapshot Current => Volatile.Read(ref _current);

    /// <summary>
    /// Raised once per publish, after <see cref="Current"/> has moved on. It is a wake-up
    /// signal, not a delivery: a subscriber reads <see cref="Current"/> rather than
    /// assuming anything about which snapshot is newest.
    /// </summary>
    /// <remarks>
    /// Handlers run on the publishing thread with the writer lock released, so a handler
    /// may publish again without deadlocking, and readers stay lock-free. The publishing
    /// thread is still occupied until every handler returns, and an exception from a
    /// handler propagates to that publisher once the snapshot is already visible.
    /// Racing publishers can also notify out of version order, so consumers must simply
    /// act on the newest snapshot.
    /// </remarks>
    public event Action? Published;

    /// <summary>
    /// Publishes a new snapshot built from <paramref name="root"/>. Returns the
    /// published instance so the caller can hand it straight to an animator.
    /// Each publication stores exactly the supplied <paramref name="rootPath"/>
    /// (null by default for legacy callers): nothing is inherited from the
    /// previous snapshot, so a restore passes the prior
    /// <see cref="TreemapSnapshot.RootPath"/> explicitly, including null when
    /// restoring to the initial placeholder. Production publishers always pass
    /// their source explicitly on previews, finals and restores.
    /// </summary>
    public TreemapSnapshot Publish(TreemapNode root, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        TreemapSnapshot snapshot;

        lock (_publishLock)
        {
            snapshot = new TreemapSnapshot(root, ++_version, DateTimeOffset.UtcNow, rootPath);

            // Release-store, issued while the writer lock is still held. The version is
            // allocated and the reference is published as one step, so two concurrent
            // publishers can never interleave into an out-of-order version sequence in
            // _current. Readers stay lock-free: they simply Volatile.Read this field.
            Volatile.Write(ref _current, snapshot);
        }

        // Outside the lock: a subscriber may republish without deadlocking against this
        // call, and readers keep running while it does.
        Published?.Invoke();
        return snapshot;
    }
}
