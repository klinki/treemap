namespace Treemap.Scanning;

/// <summary>
/// Incremental in-memory directory tree. Replaces the flat <c>List&lt;ScanEntry&gt;</c> of full
/// below-root paths: each folder name is stored once, each file is a single immutable
/// <see cref="TreemapNode"/> created once at add time and shared by every preview and the
/// final tree. Folder aggregates (and only they) are rebuilt at each capture.
/// No full path string is ever retained per file; identity uses (parentId, basename) keys.
/// Caller (ScanCollector) holds its gate around every call; this type does no locking.
/// </summary>
internal sealed class ManagedScanTree
{
    internal sealed class Folder
    {
        public string Name = string.Empty;
        public int Id;
        public DateTimeOffset? LastWriteTime;
        public Dictionary<string, Folder>? Subfolders;
        public Dictionary<string, TreemapNode>? Files;
        /// <summary>True once an explicit folder entry was recorded (inferred parents are false).</summary>
        public bool Explicit;
    }

    private readonly Folder _root = new() { Id = ScanIdMap.RootId };

    public void Clear()
    {
        _root.Subfolders = null;
        _root.Files = null;
    }

    /// <summary>
    /// Splits once; empty segments are dropped like <see cref="ScanTreeBuilder"/> does.
    /// Never returns null entries.
    /// </summary>
    public static string[] Split(string relativePath)
        => relativePath.Split(ScanPath.Separators, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Lengths of parent folders along <paramref name="segments"/> that do not exist yet,
    /// in order, without creating anything. Empty when all parents exist or a file blocks
    /// the path. The leaf is always excluded: the caller charges it once via
    /// <see cref="ScanMemoryBudget.EstimateFile"/> or
    /// <see cref="ScanMemoryBudget.EstimateFolder"/>, so including it here would charge a
    /// folder leaf twice.
    /// </summary>
    public IEnumerable<int> MissingFolderLengths(string[] segments)
    {
        Folder current = _root;
        int dirs = segments.Length - 1;
        for (int i = 0; i < dirs; i++)
        {
            string name = segments[i];
            if (current.Subfolders is not null && current.Subfolders.TryGetValue(name, out Folder? next))
            {
                current = next;
                continue;
            }

            // A file with this name blocks the folder path; nothing further is created.
            if (current.Files is not null && current.Files.ContainsKey(name))
            {
                yield break;
            }

            // Without creating we cannot descend; every deeper level nests under this new
            // folder, so the remaining directory segments are all missing too.
            for (int j = i; j < dirs; j++)
            {
                yield return segments[j].Length;
            }

            yield break;
        }
    }

    /// <summary>
    /// Ensures the parent chain for <paramref name="segments"/> exists, creating missing
    /// parents and reserving their ids when <paramref name="ids"/> is supplied. The leaf
    /// itself is never created here: the caller mutates it once via <see cref="AddFile"/>
    /// or <see cref="AddFolderLeaf"/>. Returns the parent folder that directly holds the
    /// leaf, plus how many parents were created and their summed name lengths (for honest
    /// budget charging). Stops descending through a colliding file (file wins); the
    /// returned folder is then the blocker's parent and <c>Blocked</c> is true.
    /// </summary>
    public (Folder Parent, int Created, long CreatedNameLength, bool Blocked) EnsureParents(
        string[] segments, ScanIdMap? ids)
    {
        Folder current = _root;
        int created = 0;
        long nameLength = 0;
        int dirs = segments.Length - 1;
        for (int i = 0; i < dirs; i++)
        {
            string name = segments[i];
            if (current.Subfolders is not null && current.Subfolders.TryGetValue(name, out Folder? next))
            {
                current = next;
                continue;
            }

            if (current.Files is not null && current.Files.ContainsKey(name))
            {
                return (current, created, nameLength, true);
            }

            Folder child = new() { Name = name };
            if (ids is not null)
            {
                // ReserveChild keeps the passed string as its key, so the stored basename,
                // the sibling-index key and the id-map key are one shared reference.
                child.Id = ids.ReserveChild(current.Id, name);
            }

            current.Subfolders ??= new Dictionary<string, Folder>(ScanPath.NameComparer);
            current.Subfolders[name] = child;
            created++;
            nameLength += name.Length;
            current = child;
        }

        return (current, created, nameLength, false);
    }

    public Folder Root => _root;

    /// <summary>
    /// Finds the parent folder for <paramref name="segments"/> without creating anything.
    /// The leaf is always excluded. Returns the parent when the whole parent chain
    /// exists, <c>Blocked</c> when a file owns an intermediate segment, and
    /// <c>Found</c> false when a parent is still missing (the leaf is then new).
    /// The collector peeks with this before claiming budget, so an already-recorded
    /// folder leaf costs no second retained folder.
    /// </summary>
    public (bool Found, bool Blocked, Folder? Parent) TryFindParent(string[] segments)
    {
        Folder current = _root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            string name = segments[i];
            if (current.Subfolders is not null && current.Subfolders.TryGetValue(name, out Folder? next))
            {
                current = next;
                continue;
            }

            if (current.Files is not null && current.Files.ContainsKey(name))
            {
                return (false, true, current);
            }

            return (false, false, null);
        }

        return (true, false, current);
    }

    /// <summary>True when <paramref name="parent"/> already holds a folder leaf.</summary>
    public static bool HasFolderLeaf(Folder parent, string leafName)
        => parent.Subfolders is not null && parent.Subfolders.ContainsKey(leafName);

    /// <summary>
    /// Adds a file leaf under <paramref name="parent"/>. The immutable node is created once
    /// and stored directly: no separate record duplicates its name, sizes or timestamp.
    /// Replaces a same-named file (last wins); replaces a same-named subfolder (file wins
    /// when the file entry arrives last, dropping contradictory children, matching
    /// ScanTreeBuilder.Materialise for the tree shape). A replaced subtree's descendant
    /// ids are forgotten from the pending map via <see cref="ScanIdMap.ForgetSubtree"/>,
    /// so a path absent from the returned tree gets a fresh id when it returns; the leaf
    /// key itself is reused for the file, like the legacy builder reusing one node.
    /// Returns true when this basename is new to the folder.
    /// </summary>
    public bool AddFile(
        Folder parent, string leafName, long logical, long physical,
        DateTimeOffset? lastWrite, ScanIdMap? ids)
    {
        bool isNew = parent.Files is null || !parent.Files.ContainsKey(leafName);

        if (parent.Subfolders is not null && parent.Subfolders.TryGetValue(leafName, out Folder? dropped))
        {
            parent.Subfolders.Remove(leafName);
            // Contradictory scripted order only: real backends never emit children
            // under a file. Prune so the dropped descendants do not linger into the
            // commit; without this the pending map would carry them into one extra
            // accepted rescan and a restored child would reuse its old id.
            ids?.ForgetSubtree(dropped.Id);
            isNew = true;
        }

        int id = 0;
        if (ids is not null)
        {
            id = ids.ReserveChild(parent.Id, leafName);
        }

        // The passed basename reference becomes the dictionary key, the id-map key and
        // the node's name: one string, three references, no joined-path copy.
        TreemapNode node = TreemapNode.Create(id, leafName, NodeKind.File, logical, physical, lastWrite);
        parent.Files ??= new Dictionary<string, TreemapNode>(ScanPath.NameComparer);
        parent.Files[leafName] = node;
        return isNew;
    }

    /// <summary>
    /// Marks an explicit folder leaf, preserving its timestamp. Last wins: an inferred
    /// folder gains the explicit timestamp, a duplicate explicit entry overwrites it, and
    /// a colliding file is replaced (folder wins when the folder entry arrives last),
    /// matching ScanTreeBuilder.Insert. Returns true when first recorded (caller counts
    /// the emission either way, matching the legacy flat list).
    /// </summary>
    public bool AddFolderLeaf(Folder parent, string leafName, DateTimeOffset? lastWrite, ScanIdMap? ids, out Folder folder)
    {
        if (parent.Files is not null && parent.Files.ContainsKey(leafName))
        {
            // File-then-folder: the folder entry arrives last, so it replaces the file,
            // like the legacy builder overwriting the leaf kind. The same
            // (parentId, basename) key is re-reserved, so the leaf keeps its id across
            // the kind change; a file has no descendants to prune.
            parent.Files.Remove(leafName);
            folder = new Folder { Name = leafName, Explicit = true, LastWriteTime = lastWrite };
            if (ids is not null)
            {
                folder.Id = ids.ReserveChild(parent.Id, leafName);
            }

            parent.Subfolders ??= new Dictionary<string, Folder>(ScanPath.NameComparer);
            parent.Subfolders[leafName] = folder;
            return true;
        }

        if (parent.Subfolders is not null && parent.Subfolders.TryGetValue(leafName, out Folder? existing))
        {
            folder = existing;
            // Last wins for metadata, including the inferred-to-explicit transition and
            // duplicate explicit entries. Frozen snapshots already copied the old value,
            // so earlier previews keep what they captured.
            existing.LastWriteTime = lastWrite;
            if (existing.Explicit)
            {
                return false;
            }

            existing.Explicit = true;
            return true;
        }

        folder = new Folder { Name = leafName, Explicit = true, LastWriteTime = lastWrite };
        if (ids is not null)
        {
            folder.Id = ids.ReserveChild(parent.Id, leafName);
        }

        parent.Subfolders ??= new Dictionary<string, Folder>(ScanPath.NameComparer);
        parent.Subfolders[leafName] = folder;
        return true;
    }

    /// <summary>
    /// Plain-data copy of one folder subtree: file nodes shared by reference, never cloned.
    /// Arrays hold snapshots/nodes directly (no per-entry key-value pairs): the name lives
    /// on the node/snapshot itself. One reference array per folder per capture plus the
    /// frozen child arrays inside the built immutable nodes: O(files) references transient
    /// per preview (plus O(folders) nodes), on top of the shared nodes. Old and current
    /// published snapshots overlap until the publisher moves on.
    /// </summary>
    internal sealed class FolderSnapshot
    {
        public string Name = string.Empty;
        public int Id;
        public DateTimeOffset? LastWriteTime;
        public bool Explicit;
        public FolderSnapshot[] Subfolders = [];
        public TreemapNode[] Files = [];

        public static FolderSnapshot Copy(Folder folder)
        {
            FolderSnapshot snap = new() { Name = folder.Name, Id = folder.Id, LastWriteTime = folder.LastWriteTime, Explicit = folder.Explicit };
            if (folder.Subfolders is not null && folder.Subfolders.Count > 0)
            {
                var subs = new FolderSnapshot[folder.Subfolders.Count];
                int i = 0;
                // KeyValuePair iteration avoids the per-dictionary Values view object.
                foreach (KeyValuePair<string, Folder> entry in folder.Subfolders)
                {
                    subs[i++] = Copy(entry.Value);
                }

                snap.Subfolders = subs;
            }

            if (folder.Files is not null && folder.Files.Count > 0)
            {
                var files = new TreemapNode[folder.Files.Count];
                int i = 0;
                foreach (KeyValuePair<string, TreemapNode> entry in folder.Files)
                {
                    files[i++] = entry.Value;
                }

                snap.Files = files;
            }

            return snap;
        }
    }

    /// <summary>
    /// Builds immutable folder nodes over shared file instances. Sorts combined children
    /// ordinally and saturates sums, matching ScanTreeBuilder semantics.
    /// </summary>
    public static TreemapNode BuildRoot(
        string rootPath, FolderSnapshot snap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TreemapNode[] children = BuildChildren(snap, cancellationToken);
        NodeKind kind = ScanTreeBuilder.IsVolumeRoot(rootPath) ? NodeKind.Drive : NodeKind.Folder;
        long logical = 0;
        long physical = 0;
        foreach (TreemapNode child in children)
        {
            logical = SaturatingAdd(logical, child.LogicalSize);
            physical = SaturatingAdd(physical, child.PhysicalSize);
        }

        return TreemapNode.Create(
            ScanIdMap.RootId, ScanTreeBuilder.RootName(rootPath), kind,
            logical, physical, null, 0, children);
    }

    private static TreemapNode[] BuildChildren(FolderSnapshot snap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int total = snap.Files.Length + snap.Subfolders.Length;
        if (total == 0)
        {
            return [];
        }

        TreemapNode[] kids = new TreemapNode[total];
        int at = 0;
        foreach (TreemapNode file in snap.Files)
        {
            // Created once at add time; every preview and the final tree share the instance.
            kids[at++] = file;
        }

        foreach (FolderSnapshot sub in snap.Subfolders)
        {
            TreemapNode[] grandchildren = BuildChildren(sub, cancellationToken);
            long logical = 0;
            long physical = 0;
            foreach (TreemapNode grand in grandchildren)
            {
                logical = SaturatingAdd(logical, grand.LogicalSize);
                physical = SaturatingAdd(physical, grand.PhysicalSize);
            }

            kids[at++] = TreemapNode.Create(
                sub.Id, sub.Name, NodeKind.Folder, logical, physical, sub.LastWriteTime, 0, grandchildren);
        }

        Array.Sort(kids, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return kids;
    }

    /// <summary>
    /// Final-only destructive freeze: builds the immutable root directly from the
    /// mutable working tree while releasing sibling dictionaries as each folder is
    /// frozen. Avoids holding a full <see cref="FolderSnapshot"/> copy alongside the
    /// working tree at the peak. Shared file nodes are moved by reference, never
    /// cloned; old previews holding their own immutable nodes are unaffected. The
    /// tree is empty afterwards. Caller guarantees no concurrent mutation (backends
    /// joined, previews drained) and holds the collector gate.
    /// </summary>
    public TreemapNode BuildFinalDestructive(string rootPath, CancellationToken cancellationToken)
    {
        TreemapNode[] children = BuildChildrenDestructive(_root, cancellationToken);
        _root.Subfolders = null;
        _root.Files = null;
        NodeKind kind = ScanTreeBuilder.IsVolumeRoot(rootPath) ? NodeKind.Drive : NodeKind.Folder;
        long logical = 0;
        long physical = 0;
        foreach (TreemapNode child in children)
        {
            logical = SaturatingAdd(logical, child.LogicalSize);
            physical = SaturatingAdd(physical, child.PhysicalSize);
        }

        return TreemapNode.Create(
            ScanIdMap.RootId, ScanTreeBuilder.RootName(rootPath), kind,
            logical, physical, null, 0, children);
    }

    private static TreemapNode[] BuildChildrenDestructive(Folder folder, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TreemapNode[]? fileNodes = null;
        int fileCount = 0;
        if (folder.Files is not null)
        {
            fileCount = folder.Files.Count;
            if (fileCount > 0)
            {
                fileNodes = new TreemapNode[fileCount];
                int i = 0;
                foreach (KeyValuePair<string, TreemapNode> entry in folder.Files)
                {
                    fileNodes[i++] = entry.Value;
                }
            }

            folder.Files = null;
        }

        Folder[]? subs = null;
        int subCount = 0;
        if (folder.Subfolders is not null)
        {
            subCount = folder.Subfolders.Count;
            if (subCount > 0)
            {
                subs = new Folder[subCount];
                int i = 0;
                foreach (KeyValuePair<string, Folder> entry in folder.Subfolders)
                {
                    subs[i++] = entry.Value;
                }
            }

            folder.Subfolders = null;
        }

        int total = fileCount + subCount;
        if (total == 0)
        {
            return [];
        }

        TreemapNode[] kids = new TreemapNode[total];
        int at = 0;
        if (fileNodes is not null)
        {
            foreach (TreemapNode file in fileNodes)
            {
                kids[at++] = file;
            }
        }

        if (subs is not null)
        {
            for (int i = 0; i < subs.Length; i++)
            {
                Folder sub = subs[i];
                TreemapNode[] grandchildren = BuildChildrenDestructive(sub, cancellationToken);
                long logical = 0;
                long physical = 0;
                foreach (TreemapNode grand in grandchildren)
                {
                    logical = SaturatingAdd(logical, grand.LogicalSize);
                    physical = SaturatingAdd(physical, grand.PhysicalSize);
                }

                kids[at++] = TreemapNode.Create(
                    sub.Id, sub.Name, NodeKind.Folder, logical, physical, sub.LastWriteTime, 0, grandchildren);
                // Release the mutable folder as soon as its immutable node exists so a
                // wide final build frees sibling subtrees incrementally.
                subs[i] = null!;
            }
        }

        Array.Sort(kids, static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return kids;
    }

    private static long SaturatingAdd(long running, long value)
    {
        long add = value > 0 ? value : 0;
        return running > long.MaxValue - add ? long.MaxValue : running + add;
    }

    /// <summary>Enumerates reconstructed entries (full below-root paths) for legacy APIs/tests.</summary>
    public IEnumerable<ScanEntry> EnumerateEntries()
    {
        List<string> prefix = [];
        return EnumerateUnder(_root, prefix);
    }

    private static IEnumerable<ScanEntry> EnumerateUnder(Folder folder, List<string> prefix)
    {
        if (folder.Files is not null)
        {
            foreach (TreemapNode node in folder.Files.Values)
            {
                yield return new ScanEntry(
                    Join(prefix, node.Name), NodeKind.File, node.LogicalSize, node.PhysicalSize, node.LastWriteTime);
            }
        }

        if (folder.Subfolders is not null)
        {
            foreach (Folder sub in folder.Subfolders.Values)
            {
                // Only explicitly recorded folders appear as entries, matching the old flat
                // list; inferred parents exist for structure only.
                if (sub.Explicit)
                {
                    yield return new ScanEntry(Join(prefix, sub.Name), NodeKind.Folder, 0, 0, sub.LastWriteTime);
                }

                prefix.Add(sub.Name);
                foreach (ScanEntry e in EnumerateUnder(sub, prefix))
                {
                    yield return e;
                }

                prefix.RemoveAt(prefix.Count - 1);
            }
        }
    }

    private static string Join(List<string> prefix, string name)
    {
        if (prefix.Count == 0)
        {
            return name;
        }

        return string.Join(Path.DirectorySeparatorChar, prefix) + Path.DirectorySeparatorChar + name;
    }
}
