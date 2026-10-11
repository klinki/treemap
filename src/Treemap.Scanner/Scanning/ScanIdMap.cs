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
}
