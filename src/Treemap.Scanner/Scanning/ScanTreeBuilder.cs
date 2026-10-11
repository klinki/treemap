namespace Treemap.Scanning;

/// <summary>
/// Turns the flat list of relative-path entries a backend produced into an immutable tree.
/// Shared: the MFT reader arrives at the same shape by a different route, and neither
/// backend should own tree assembly.
/// </summary>
internal static class ScanTreeBuilder
{
    public static TreemapNode Build(
        string rootPath,
        IReadOnlyList<ScanEntry> entries,
        ScanIdMap ids,
        CancellationToken cancellationToken)
    {
        MutableNode root = new()
        {
            Name = RootName(rootPath),
            Kind = IsVolumeRoot(rootPath) ? NodeKind.Drive : NodeKind.Folder,
        };

        foreach (ScanEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Insert(root, entry);
        }

        // Sorted so the tree is in a stable order regardless of backend or worker
        // interleaving. Ids come from the map, so this is presentation only - but a
        // jittering child order makes the whole treemap jump.
        SortChildren(root, cancellationToken);

        return Materialise(root, ScanIdMap.RootId, FullPath(rootPath), ids, cancellationToken);
    }

    /// <summary>
    /// The name to show for the scan root: the volume label without its trailing
    /// separator (<c>C:</c>), or the folder's own name. A bare root separator is kept,
    /// because nothing would be left to show instead.
    /// </summary>
    public static string RootName(string rootPath)
    {
        string full = ScanPath.Normalize(rootPath);
        string? volume = Path.GetPathRoot(full);

        if (volume is null || !string.Equals(volume, full, ScanPath.NameComparison))
        {
            return Path.GetFileName(full);
        }

        return full.Length > 1 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool IsVolumeRoot(string rootPath)
    {
        string full = ScanPath.Normalize(rootPath);
        return string.Equals(Path.GetPathRoot(full), full, ScanPath.NameComparison);
    }

    public static string FullPath(string rootPath) => ScanPath.Normalize(rootPath);

    /// <summary>
    /// Walks the entry's path segments, materialising the folders it passes through.
    /// Entries can arrive in any order, so an intermediate folder is created on demand.
    /// </summary>
    private static void Insert(MutableNode root, ScanEntry entry)
    {
        string[] segments = entry.RelativePath.Split(ScanPath.Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        MutableNode parent = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            parent = FindOrAddChild(parent, segments[i]);
        }

        MutableNode leaf = FindOrAddChild(parent, segments[^1]);
        leaf.Kind = entry.Kind;
        leaf.LogicalSize = entry.LogicalSize;
        leaf.PhysicalSize = entry.PhysicalSize;
        leaf.LastWriteTime = entry.LastWriteTime;
    }

    private static MutableNode FindOrAddChild(MutableNode parent, string name)
    {
        if (parent.Index.TryGetValue(name, out MutableNode? existing))
        {
            return existing;
        }

        MutableNode created = new() { Name = name, Kind = NodeKind.Folder };
        parent.Index[name] = created;
        parent.Children.Add(created);
        return created;
    }

    private static void SortChildren(MutableNode node, CancellationToken cancellationToken)
    {
        // Checked per folder rather than per entry: the sort is the one pass left after
        // the entries are in, and a cancelled scan should not walk a wide tree to the end.
        cancellationToken.ThrowIfCancellationRequested();

        if (node.Children.Count > 1)
        {
            node.Children.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        foreach (MutableNode child in node.Children)
        {
            SortChildren(child, cancellationToken);
        }
    }

    private static TreemapNode Materialise(
        MutableNode node,
        int id,
        string absolutePath,
        ScanIdMap ids,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        node.Id = id;

        // A node cannot be both a file and a folder. If the filesystem claims
        // both, the file entry wins and the contradictory children are dropped rather
        // than rendering as a file named after a folder with a file's zero size.
        if (node.Kind == NodeKind.File || node.Children.Count == 0)
        {
            long size = node.Kind == NodeKind.File ? node.LogicalSize : 0;
            long allocated = node.Kind == NodeKind.File ? node.PhysicalSize : 0;
            return TreemapNode.Create(node.Id, node.Name, node.Kind, size, allocated, node.LastWriteTime);
        }

        TreemapNode[] children = new TreemapNode[node.Children.Count];
        for (int i = 0; i < children.Length; i++)
        {
            MutableNode child = node.Children[i];
            string childPath = Path.Combine(absolutePath, child.Name);
            children[i] = Materialise(child, ids.Reserve(childPath), childPath, ids, cancellationToken);
        }

        // The children are already materialised, so their sizes are summed here instead of
        // handing Create the 0/0 that asks it to aggregate. Create's own sum is unchecked:
        // two files that are each a legal size can still add up to more than a long holds,
        // and a total that wrapped negative would be a worse lie than one that saturates.
        long logical = 0;
        long physical = 0;

        foreach (TreemapNode child in children)
        {
            logical = SaturatingAdd(logical, child.LogicalSize);
            physical = SaturatingAdd(physical, child.PhysicalSize);
        }

        // The node keeps its kind, so a volume root stays a Drive instead of being flattened
        // into a Folder. When both totals come out 0 (a tree of empty files) Create still
        // re-sums, which lands on the same answer.
        return TreemapNode.Create(node.Id, node.Name, node.Kind, logical, physical, node.LastWriteTime, 0, children);
    }

    /// <summary>
    /// Sums without wrapping, matching <see cref="ScanCollector"/>'s totals so the tree and
    /// the result never disagree about how large the same scan was.
    /// </summary>
    private static long SaturatingAdd(long running, long value)
    {
        long add = value > 0 ? value : 0;
        return running > long.MaxValue - add ? long.MaxValue : running + add;
    }

    private sealed class MutableNode
    {
        public string Name { get; init; } = string.Empty;

        public NodeKind Kind { get; set; } = NodeKind.Folder;

        public long LogicalSize { get; set; }

        public long PhysicalSize { get; set; }

        public DateTimeOffset? LastWriteTime { get; set; }

        public int Id { get; set; }

        public List<MutableNode> Children { get; } = [];

        /// <summary>
        /// Sibling lookup. A linear search per entry is quadratic in the size of a folder,
        /// which is exactly the shape a directory of 500,000 files has.
        /// </summary>
        public Dictionary<string, MutableNode> Index { get; } = new(ScanPath.NameComparer);
    }
}