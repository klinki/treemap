namespace Treemap.Scanning;

/// <summary>
/// Hands out <see cref="TreemapNode.Id"/> values that stay attached to the same absolute
/// path from one returned tree to the next.
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
/// </remarks>
internal sealed class ScanIdMap
{
    /// <summary>The scan root's id, outside the map.</summary>
    public const int RootId = 1;

    private readonly Lock _gate = new();
    private Dictionary<string, int> _committed = new(ScanPath.NameComparer);
    private Dictionary<string, int>? _pending;

    // Ids 0 and 1 belong to the root, which is not in the map, so the first reserved id
    // is 2. Numbers are never reused: a path that disappears and comes back is a new
    // node, and reusing its number would let a stale snapshot claim it.
    private long _nextId = RootId + 1;

    /// <summary>Opens a build. Any previous uncommitted build is discarded.</summary>
    public void BeginBuild()
    {
        lock (_gate)
        {
            _pending = new Dictionary<string, int>(_committed.Count + 64, ScanPath.NameComparer);
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
                _pending = null;
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
        }
    }

    /// <summary>
    /// Returns the id bound to <paramref name="absolutePath"/>, reusing the committed one
    /// when the path was in the previous tree.
    /// </summary>
    /// <exception cref="InvalidOperationException">No build is open, or the 32-bit id space is exhausted.</exception>
    public int Reserve(string absolutePath)
    {
        lock (_gate)
        {
            Dictionary<string, int> pending = _pending
                ?? throw new InvalidOperationException("No scan build is open.");

            if (pending.TryGetValue(absolutePath, out int id) || _committed.TryGetValue(absolutePath, out id))
            {
                pending[absolutePath] = id;
                return id;
            }

            // Never clamp: a saturated id would silently alias every later node onto one
            // entry and make the tree unclickable. Running out is a bug to hear about.
            if (_nextId >= int.MaxValue)
            {
                throw new InvalidOperationException(
                    "Node id space exhausted. This scanner instance has seen more distinct paths than a 32-bit id can address; create a new FileSystemScanner.");
            }

            id = (int)_nextId++;
            pending[absolutePath] = id;
            return id;
        }
    }

    /// <summary>
    /// Id for the synthetic free-space block of a volume-root scan. Keyed on a path no
    /// filesystem can produce - NUL is illegal in a path on every platform the scanner
    /// runs on - so it can never collide with a real directory entry.
    /// </summary>
    public int ReserveFreeSpace(string rootPath) => Reserve(rootPath + "\0#free-space");

    /// <summary>
    /// Id for the synthetic unaccounted-space block, keyed like
    /// <see cref="ReserveFreeSpace(string)"/> under its own suffix so the two blocks never
    /// share an id with each other or with a real entry.
    /// </summary>
    public int ReserveUnknownSpace(string rootPath) => Reserve(rootPath + "\0#unknown-space");
}
