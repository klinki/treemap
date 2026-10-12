namespace Treemap.Scanning;

/// <summary>
/// Hands out <see cref="TreemapNode.Id"/> values that stay attached to the same path
/// from one returned tree to the next. Production builds reserve below-root paths
/// scoped to their scan root (<see cref="BeginBuild(string)"/>); the absolute-path
/// entry point remains for the legacy contract.
/// </summary>
/// <remarks>
/// <para>
/// Not positional numbering: the controller keeps focus by node id, so adding one sibling
/// would renumber everything after it and move the focus onto a different folder.
/// </para>
///
/// <para>
/// <b>Guaranteed:</b> a path present in two consecutive returned trees keeps its id in
/// both. That is narrower than "stable forever" - a path that disappeared and came back is
/// a new node, because the tree in between never showed it. Only paths the new tree
/// contains are carried over, so retention needs no bookkeeping.
/// </para>
///
/// <para>
/// A build becomes the committed map only once a result exists, so a cancelled or failed
/// scan cannot rewrite the identity of the tree the user is looking at. The scan root is
/// id 1 and is in neither map; keys use <see cref="ScanPath.NameComparer"/>.
/// </para>
///
/// <para>
/// One active transaction: a single pending map plus the root that scopes it (null for
/// the legacy absolute-path mode). Opening a build discards any uncommitted one,
/// including one opened in the other mode. Committed ids are reused only when the
/// committed root matches the pending one - same root and same mode - so a different
/// root never reuses a wrong path's id.
/// </para>
/// </remarks>
internal sealed class ScanIdMap
{
    /// <summary>The scan root's id, outside the map.</summary>
    public const int RootId = 1;

    private readonly Lock _gate = new();
    private Dictionary<string, int> _committed = new(ScanPath.NameComparer);

    /// <summary>
    /// The root the committed map belongs to, or null for a legacy absolute-path
    /// commit. Null until the first scoped commit; only replaced by a commit, so an
    /// aborted rescan of another root leaves it - and its ids - untouched.
    /// </summary>
    private string? _committedRoot;

    private Dictionary<string, int>? _pending;

    /// <summary>Null for a legacy build, otherwise the normalized scoped root.</summary>
    private string? _pendingRoot;

    /// <summary>
    /// Hierarchical identities for the incremental tree: (parentId, basename) instead of
    /// whole below-root paths, so no full path string is retained per file. Shares
    /// <see cref="_nextId"/> with the legacy string maps; both modes commit/abort together.
    /// Keys hold the basename reference shared with the tree node, never a joined path.
    /// </summary>
    private Dictionary<ChildKey, int>? _pendingChildren;
    private Dictionary<ChildKey, int> _committedChildren = new(ChildKey.Comparer);

    // Ids 0 and 1 belong to the root, which is not in the map, so the first reserved id
    // is 2. Numbers are never reused: a path that disappears and comes back is a new
    // node, and reusing its number would let a stale snapshot claim it.
    private long _nextId = RootId + 1;

    /// <summary>Opens a legacy absolute-path build, discarding any uncommitted build.</summary>
    public void BeginBuild() => OpenBuild(null);

    /// <summary>Opens a root-scoped build for <see cref="ReserveRelative(string)"/>.</summary>
    public void BeginBuild(string rootPath) => OpenBuild(ScanPath.Normalize(rootPath));

    private void OpenBuild(string? root)
    {
        lock (_gate)
        {
            bool match = root is null
                ? _committedRoot is null
                : _committedRoot is not null && string.Equals(_committedRoot, root, ScanPath.NameComparison);
            _pending = new Dictionary<string, int>(match ? _committed.Count + 64 : 64, ScanPath.NameComparer);
            _pendingRoot = root;
            // Scoped builds reserve hierarchical (parentId, basename) ids plus the two
            // synthetic space ids (string map); legacy builds need no child map. A rescan
            // peak holds the committed maps plus the growing pending ones (about 2x ids)
            // until commit replaces them; the memory estimate accounts for both.
            _pendingChildren = root is null
                ? null
                : new Dictionary<ChildKey, int>(
                    match ? _committedChildren.Count + 64 : 64, ChildKey.Comparer);
        }
    }

    /// <summary>Promotes the current build. Call only once a result exists.</summary>
    public void Commit()
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                _committed = _pending;
                _committedRoot = _pendingRoot;
                _pending = null;
                _pendingRoot = null;
            }

            if (_pendingChildren is not null)
            {
                _committedChildren = _pendingChildren;
                _pendingChildren = null;
            }
        }
    }

    /// <summary>
    /// Drops the current build without promoting it. Called from where a commit would have
    /// happened, so a failed or cancelled scan leaves no half-read mapping behind.
    /// </summary>
    public void Abort()
    {
        lock (_gate)
        {
            _pending = null;
            _pendingRoot = null;
            _pendingChildren = null;
        }
    }

    /// <summary>
    /// Returns the id bound to <paramref name="absolutePath"/>. Legacy mode only.
    /// </summary>
    /// <exception cref="InvalidOperationException">No legacy build is open, or the 32-bit id space is exhausted.</exception>
    public int Reserve(string absolutePath)
    {
        lock (_gate)
        {
            if (_pending is null || _pendingRoot is not null)
            {
                throw new InvalidOperationException("No scan build is open.");
            }

            return ReserveLocked(absolutePath, _committedRoot is null);
        }
    }

    /// <summary>
    /// Returns the id bound to <paramref name="relativePath"/> below the build's root.
    /// The caller passes the canonical below-root spelling and may share the
    /// <see cref="ScanEntry.RelativePath"/> string itself: the map keeps the reference.
    /// </summary>
    /// <exception cref="InvalidOperationException">No scoped build is open, or the 32-bit id space is exhausted.</exception>
    public int ReserveRelative(string relativePath)
    {
        string key = CanonicalRelative(relativePath);
        lock (_gate)
        {
            string? pendingRoot = _pendingRoot;
            if (_pending is null || pendingRoot is null)
            {
                throw new InvalidOperationException("No scan build is open.");
            }

            bool reuse = _committedRoot is not null
                && string.Equals(_committedRoot, pendingRoot, ScanPath.NameComparison);
            return ReserveLocked(key, reuse);
        }
    }

    // Caller holds the gate. Reuses the pending id, else the committed one when allowed,
    // else mints a fresh id. Never clamps: a saturated id would alias later nodes.
    private int ReserveLocked(string key, bool reuseCommitted)
    {
        Dictionary<string, int> pending = _pending!;
        if (pending.TryGetValue(key, out int id)
            || (reuseCommitted && _committed.TryGetValue(key, out id)))
        {
            pending[key] = id;
            return id;
        }

        if (_nextId >= int.MaxValue)
        {
            throw new InvalidOperationException(
                "Node id space exhausted. This scanner instance has seen more distinct paths than a 32-bit id can address; create a new FileSystemScanner.");
        }

        id = (int)_nextId++;
        pending[key] = id;
        return id;
    }

    /// <summary>Scoped map key: folds the alternate separator to the primary one.</summary>
    private static string CanonicalRelative(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return relativePath;
        }

        char primary = Path.DirectorySeparatorChar;
        char alt = Path.AltDirectorySeparatorChar;
        foreach (char c in relativePath)
        {
            if (c == alt && alt != primary)
            {
                return relativePath.Replace(alt, primary);
            }
        }

        return relativePath;
    }

    /// <summary>Id for the synthetic free-space block of the build's root scan.</summary>
    /// <remarks>
    /// Keyed on a path no filesystem can produce - NUL is illegal in a path on every
    /// platform the scanner runs on - so it can never collide with a real entry.
    /// </remarks>
    public int ReserveFreeSpaceScoped() => ReserveRelative("\0#free-space");

    /// <summary>
    /// Id for the synthetic unaccounted-space block, keyed like
    /// <see cref="ReserveFreeSpaceScoped"/> under its own suffix so the two blocks never
    /// share an id with each other or with a real entry.
    /// </summary>
    public int ReserveUnknownSpaceScoped() => ReserveRelative("\0#unknown-space");

    /// <summary>
    /// Returns the id bound to (<paramref name="parentId"/>, <paramref name="name"/>) for the
    /// incremental tree. No joined path is built or retained: siblings share nothing but
    /// their parent's id. Case follows <see cref="ScanPath.NameComparer"/>, so Windows
    /// aliases agree and Unix siblings stay distinct. Committed ids are reused only for the
    /// same scoped root, like <see cref="ReserveRelative(string)"/>.
    /// The caller must pass the same basename reference it stores on the node so the map
    /// shares it instead of copying.
    /// </summary>
    /// <exception cref="InvalidOperationException">No scoped build is open, or the 32-bit id space is exhausted.</exception>
    public int ReserveChild(int parentId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ChildKey key = new(parentId, name);
        lock (_gate)
        {
            Dictionary<ChildKey, int>? pending = _pendingChildren;
            string? pendingRoot = _pendingRoot;
            if (pending is null || pendingRoot is null)
            {
                throw new InvalidOperationException("No scan build is open.");
            }

            if (pending.TryGetValue(key, out int id))
            {
                return id;
            }

            bool reuse = _committedRoot is not null
                && string.Equals(_committedRoot, pendingRoot, ScanPath.NameComparison);
            if (reuse && _committedChildren.TryGetValue(key, out id))
            {
                pending[key] = id;
                return id;
            }

            if (_nextId >= int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Node id space exhausted. This scanner instance has seen more distinct paths than a 32-bit id can address; create a new FileSystemScanner.");
            }

            id = (int)_nextId++;
            pending[key] = id;
            return id;
        }
    }

    /// <summary>
    /// Forgets every pending reservation strictly below <paramref name="folderId"/>.
    /// The folder's own (parentId, basename) key stays: a file replacing a folder reuses
    /// it for the new leaf. Only the pending map is touched, so an aborted scan keeps
    /// its committed ids. Called when a file entry replaces a subfolder that already
    /// holds children (contradictory scripted order; real backends never emit it), so a
    /// path absent from the returned tree gets a fresh id when it returns instead of
    /// lingering through the commit into one extra accepted rescan.
    /// Rare path; a linear pass per level is fine.
    /// </summary>
    public void ForgetSubtree(int folderId)
    {
        lock (_gate)
        {
            Dictionary<ChildKey, int>? pending = _pendingChildren;
            if (pending is null || pending.Count == 0)
            {
                return;
            }

            HashSet<int> condemned = [folderId];
            bool removed;
            do
            {
                removed = false;
                List<ChildKey> dead = [];
                foreach (var entry in pending)
                {
                    if (condemned.Contains(entry.Key.ParentId))
                    {
                        dead.Add(entry.Key);
                    }
                }

                foreach (ChildKey key in dead)
                {
                    if (pending.Remove(key, out int childId))
                    {
                        condemned.Add(childId);
                        removed = true;
                    }
                }
            }
            while (removed);
        }
    }

    /// <summary>
    /// Hierarchical identity key. Equality folds basename case per platform; the parent id
    /// keeps same-named files in different folders distinct without any path string.
    /// </summary>
    private readonly struct ChildKey(int parentId, string name) : IEquatable<ChildKey>
    {
        public static readonly IEqualityComparer<ChildKey> Comparer = new ChildKeyComparer();

        public readonly int ParentId = parentId;
        public readonly string Name = name;

        public bool Equals(ChildKey other)
            => ParentId == other.ParentId && ScanPath.NameComparer.Equals(Name, other.Name);

        public override bool Equals(object? obj) => obj is ChildKey other && Equals(other);

        public override int GetHashCode()
            => HashCode.Combine(ParentId, ScanPath.NameComparer.GetHashCode(Name));

        private sealed class ChildKeyComparer : IEqualityComparer<ChildKey>
        {
            public bool Equals(ChildKey x, ChildKey y) => x.Equals(y);

            public int GetHashCode(ChildKey obj) => obj.GetHashCode();
        }
    }
}
