using System.Diagnostics.CodeAnalysis;

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

        return Materialise(root, ScanIdMap.RootId, ids, cancellationToken);
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
    /// Each node remembers its canonical below-root path, built once from its parent's,
    /// so identity reservation needs no absolute path and no second walk of the tree.
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
            if (!TryGetChild(parent, segments[i], out MutableNode? next))
            {
                next = AddChild(parent, segments[i], ScanPath.Join(parent.RelativePath, segments[i]));
            }

            parent = next;
        }

        // The collector already holds this exact string in the common case, so share
        // it as the node's path - and as the id map's key - instead of joining a copy.
        // Anything else (an alternate separator, a doubled one) is re-joined into the
        // canonical spelling the parent chain already carries.
        string leafName = segments[^1];
        string leafRelative = IsCanonicalRelative(entry.RelativePath)
            ? entry.RelativePath
            : ScanPath.Join(parent.RelativePath, leafName);

        MutableNode leaf;
        if (!TryGetChild(parent, leafName, out MutableNode? existing))
        {
            leaf = AddChild(parent, leafName, leafRelative);
        }
        else
        {
            leaf = existing;
        }

        leaf.Kind = entry.Kind;
        leaf.LogicalSize = entry.LogicalSize;
        leaf.PhysicalSize = entry.PhysicalSize;
        leaf.LastWriteTime = entry.LastWriteTime;
    }

    /// <summary>
    /// True when <paramref name="relativePath"/> is already the canonical below-root
    /// spelling: joined with the platform separator, with no empty segments and no
    /// alternate separators. Only then may the caller share the string as-is.
    /// </summary>
    private static bool IsCanonicalRelative(string relativePath)
    {
        if (relativePath.Length == 0)
        {
            return false;
        }

        char primary = Path.DirectorySeparatorChar;
        char alt = Path.AltDirectorySeparatorChar;
        bool prevSeparator = true;

        foreach (char c in relativePath)
        {
            if (c == primary)
            {
                if (prevSeparator)
                {
                    return false;
                }

                prevSeparator = true;
            }
            else if (c == alt)
            {
                // When the two separators differ this is an alias, not the canonical
                // spelling; when they are the same the branch above already handled it.
                return false;
            }
            else
            {
                prevSeparator = false;
            }
        }

        return !prevSeparator;
    }

    private static bool TryGetChild(MutableNode parent, string name, [NotNullWhen(true)] out MutableNode? child)
    {
        if (parent.Index is not null && parent.Index.TryGetValue(name, out child))
        {
            return true;
        }

        child = null;
        return false;
    }

    private static MutableNode AddChild(MutableNode parent, string name, string relativePath)
    {
        MutableNode created = new() { Name = name, Kind = NodeKind.Folder, RelativePath = relativePath };
        parent.Children ??= new List<MutableNode>();
        parent.Children.Add(created);
        parent.Index ??= new Dictionary<string, MutableNode>(ScanPath.NameComparer);
        parent.Index[name] = created;
        return created;
    }

    private static void SortChildren(MutableNode node, CancellationToken cancellationToken)
    {
        // Checked per folder rather than per entry: the sort is the one pass left after
        // the entries are in, and a cancelled scan should not walk a wide tree to the end.
        cancellationToken.ThrowIfCancellationRequested();

        if (node.Children is { Count: > 1 } children)
        {
            children.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        if (node.Children is null)
        {
            return;
        }

        foreach (MutableNode child in node.Children)
        {
            SortChildren(child, cancellationToken);
        }
    }

    private static TreemapNode Materialise(
        MutableNode node,
        int id,
        ScanIdMap ids,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        node.Id = id;

        // A node cannot be both a file and a folder. If the filesystem claims
        // both, the file entry wins and the contradictory children are dropped rather
        // than rendering as a file named after a folder with a file's zero size.
        if (node.Kind == NodeKind.File || node.Children is null || node.Children.Count == 0)
        {
            long size = node.Kind == NodeKind.File ? node.LogicalSize : 0;
            long allocated = node.Kind == NodeKind.File ? node.PhysicalSize : 0;
            return TreemapNode.Create(node.Id, node.Name, node.Kind, size, allocated, node.LastWriteTime);
        }

        TreemapNode[] children = new TreemapNode[node.Children.Count];
        for (int i = 0; i < children.Length; i++)
        {
            MutableNode child = node.Children[i];
            children[i] = Materialise(child, ids.ReserveRelative(child.RelativePath), ids, cancellationToken);
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

        /// <summary>
        /// Canonical path below the scan root, built once from the parent's. The root
        /// itself is the empty string. A file whose entry path is already canonical
        /// shares the collector's string here, so the id map can key on it without a
        /// second copy.
        /// </summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>
        /// Null until the first child arrives. Files stay childless, so a flat volume
        /// of half a million files allocates no per-file lists or sibling indexes -
        /// only the folders that actually have children pay for them.
        /// </summary>
        public List<MutableNode>? Children { get; set; }

        /// <summary>
        /// Sibling lookup. A linear search per entry is quadratic in the size of a folder,
        /// which is exactly the shape a directory of 500,000 files has.
        /// </summary>
        public Dictionary<string, MutableNode>? Index { get; set; }
    }
}