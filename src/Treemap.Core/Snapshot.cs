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
    private readonly struct Lookup(int parentId, TreemapNode node)
    {
        public readonly int ParentId = parentId;
        public readonly TreemapNode Node = node;
    }

    /// <summary>
    /// One page of a paged index. Each page holds 4096 ids: 32KB of references
    /// plus 16KB of parent ids, both below the large-object heap so a 3.5M-node
    /// production tree never needs a single ~140MB contiguous dictionary array.
    /// Pages are filled during construction only and never mutated afterwards.
    /// </summary>
    private sealed class Page
    {
        public readonly TreemapNode?[] Nodes = new TreemapNode?[PageSize];
        public readonly int[] Parents = new int[PageSize];
    }

    private const int PageShift = 12;
    private const int PageSize = 1 << PageShift;
    private const int PageMask = PageSize - 1;

    /// <summary>
    /// At least half of each page is used on average so paging reduces memory. Dense
    /// production ids average thousands per page; sparse or tiny trees stay on
    /// the dictionary fallback so one node never costs a full 48KB page.
    /// </summary>
    private const int MinAvgEntriesPerPage = PageSize / 2;

    /// <summary>
    /// One index for both lookups: id to (node, parent). Dense production trees
    /// use the paged directory; small or very sparse trees use the dictionary.
    /// Exactly one of the two is non-null. The root's parent is -1.
    /// </summary>
    private readonly Dictionary<int, Page>? _pages;
    private readonly Dictionary<int, Lookup>? _sparse;
    private readonly int _nodeCount;

    public TreemapSnapshot(TreemapNode root, long version, DateTimeOffset timestamp, string? rootPath = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        Root = root;
        Version = version;
        Timestamp = timestamp;
        RootPath = rootPath;

        // Counting pass sizes the index without resize overlap and collects the
        // distinct page keys (tiny for dense production ids) to pick the index shape.
        int visits = 0;
        HashSet<int> pageKeys = [];
        CountVisits(root, ref visits, pageKeys);

        // Average occupancy from visits (duplicates harmlessly over-count) over
        // distinct pages. A root outlier (id 1 with descendants at 100M+) adds a
        // single extra page key, so dense production trees still page; min/max id
        // range alone is never used because it would falsely fall back there.
        if (pageKeys.Count > 0 && (long)visits >= (long)pageKeys.Count * MinAvgEntriesPerPage)
        {
            Dictionary<int, Page> pages = new(pageKeys.Count);
            int unique = 0;
            IndexPaged(pages, root, parentId: -1, ref unique);
            _pages = pages;
            _nodeCount = unique;
        }
        else
        {
            // Small or very sparse trees: a dictionary avoids one 48KB page per node.
            Dictionary<int, Lookup> map = new(visits);
            Index(map, root, parentId: -1);
            _sparse = map;
            _nodeCount = map.Count;
        }
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

    public int NodeCount => _nodeCount;

    /// <summary>
    /// Single lookup path for both index shapes. Never exposes mutable backing arrays.
    /// </summary>
    private bool TryLookup(int id, out TreemapNode node, out int parentId)
    {
        if (_pages is not null)
        {
            int pageKey = id >> PageShift;
            int offset = id & PageMask;
            if (_pages.TryGetValue(pageKey, out Page? page))
            {
                TreemapNode? found = page.Nodes[offset];
                if (found is not null)
                {
                    node = found;
                    parentId = page.Parents[offset];
                    return true;
                }
            }

            node = null!;
            parentId = 0;
            return false;
        }

        if (_sparse!.TryGetValue(id, out Lookup foundSparse))
        {
            node = foundSparse.Node;
            parentId = foundSparse.ParentId;
            return true;
        }

        node = null!;
        parentId = 0;
        return false;
    }

    public bool TryGetNode(int id, out TreemapNode node)
    {
        if (TryLookup(id, out TreemapNode found, out _))
        {
            node = found;
            return true;
        }

        node = null!;
        return false;
    }

    /// <summary>
    /// The true parent of a node in the whole snapshot, indexed once per publish.
    /// Returns false for the root and for unknown ids.
    /// </summary>
    public bool TryGetParent(int id, out int parentId)
    {
        if (TryLookup(id, out TreemapNode found, out int foundParent) && !ReferenceEquals(found, Root))
        {
            parentId = foundParent;
            return true;
        }

        parentId = 0;
        return false;
    }

    /// <summary>
    /// Display path of a node, built from the snapshot alone with no filesystem I/O.
    /// With a <see cref="RootPath"/>, the root resolves to that absolute source and
    /// descendants append with the source's own separator style; otherwise segments
    /// are joined with '/' and the root comes first.
    /// </summary>
    public string GetPath(int id)
    {
        if (!TryLookup(id, out TreemapNode found, out _))
        {
            return id.ToString(CultureInfo.InvariantCulture);
        }

        List<string> segments = [found.Name];
        int current = id;
        while (TryGetParent(current, out int stepParent)
            && TryLookup(stepParent, out TreemapNode parentNode, out _))
        {
            segments.Add(parentNode.Name);
            current = stepParent;
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
    /// Counting pass: visits size the index without resize overlap, and distinct
    /// page keys pick the paged versus dictionary shape. Duplicate ids count once
    /// per visit: slight over-sizing is harmless.
    /// </summary>
    private static void CountVisits(TreemapNode node, ref int visits, HashSet<int> pageKeys)
    {
        checked
        {
            visits++;
        }

        pageKeys.Add(node.Id >> PageShift);

        IReadOnlyList<TreemapNode> children = node.Children;
        for (int i = 0; i < children.Count; i++)
        {
            CountVisits(children[i], ref visits, pageKeys);
        }
    }

    private static void IndexPaged(Dictionary<int, Page> pages, TreemapNode node, int parentId, ref int unique)
    {
        int pageKey = node.Id >> PageShift;
        int offset = node.Id & PageMask;
        if (!pages.TryGetValue(pageKey, out Page? page))
        {
            page = new Page();
            pages[pageKey] = page;
        }

        // Last depth-first visit wins; NodeCount grows only for a fresh slot.
        if (page.Nodes[offset] is null)
        {
            unique++;
        }

        page.Nodes[offset] = node;
        page.Parents[offset] = parentId;

        foreach (TreemapNode child in node.Children)
        {
            IndexPaged(pages, child, node.Id, ref unique);
        }
    }

    private static void Index(Dictionary<int, Lookup> map, TreemapNode node, int parentId)
    {
        map[node.Id] = new Lookup(parentId, node);

        foreach (TreemapNode child in node.Children)
        {
            Index(map, child, node.Id);
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
