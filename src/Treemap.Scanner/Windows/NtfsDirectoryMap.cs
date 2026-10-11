namespace Treemap.Scanning.Windows;

/// <summary>Why an entry's parent reference could not be used.</summary>
internal enum MftParentStatus
{
    /// <summary>The parent directory is known and was reached.</summary>
    Resolved,

    /// <summary>The record pointed at a directory the map never saw.</summary>
    UnknownParent,

    /// <summary>
    /// The record expected a different generation of that directory: the record number has
    /// been reused, so the link describes a file that no longer lives there.
    /// </summary>
    StaleParent,

    /// <summary>The record claims to be inside itself.</summary>
    CyclicParent,
}

/// <summary>
/// The directories one volume's MFT describes, and the subtree a caller asked for.
/// </summary>
/// <remarks>
/// <para>
/// The MFT is a flat table with the tree encoded in parent references, so the tree is rebuilt
/// before anything can be published. Two passes do that: the first keeps directories, the
/// second streams every record and keeps entries whose parent fell in range. One pass would
/// have to hold a path for every file on the volume, which for a subdirectory scan is most of
/// a disk's memory to answer a question about one folder.
/// </para>
/// <para>
/// Directories are retained under the caller's own entry budget; a volume with more than that
/// is declined rather than read into an unbounded dictionary. Record numbers carry no
/// hierarchy, so the only thing ordering this structure is the parent references themselves,
/// checked against the generation of the parent they name.
/// </para>
/// </remarks>
internal sealed class NtfsDirectoryMap
{
    private sealed class Node
    {
        public ulong Ordinal { get; init; }

        public ulong Sequence { get; set; }

        public ulong ParentOrdinal { get; set; }

        public ulong ParentSequence { get; set; }

        public string Name { get; set; } = string.Empty;

        public FileAttributes Attributes { get; set; }

        public bool IsReparsePoint { get; set; }

        public bool InSubtree { get; set; }

        public int Depth { get; set; }

        /// <summary>Path below the scan root. Empty until the directory is marked in range.</summary>
        public string RelativePath { get; set; } = string.Empty;
    }

    /// <summary>Directories deliberately left out, counted rather than enumerated.</summary>
    public sealed class Exclusions
    {
        public long LinksNotFollowed { get; set; }

        public long BeyondDepth { get; set; }

        public long StaleDirectories { get; set; }
    }

    private readonly Dictionary<ulong, Node> _directories = [];
    private readonly List<Node> _all = [];
    private readonly long _limit;
    private readonly ScanCollector? _collector;
    private Dictionary<ulong, List<Node>>? _children;
    private bool _volumeRootRead;

    public NtfsDirectoryMap(long limit, ScanCollector? collector = null)
    {
        _limit = limit;
        _collector = collector;

        // A place for the volume root so a path can be resolved as the walk proceeds. It
        // starts unpopulated: nothing may be built on record 5 until record 5 has actually
        // been read and its sequence known, or a volume whose root record is missing would
        // quietly produce a tree with nothing under it.
        _directories.Add(MftRecordParser.VolumeRootRecord, new Node
        {
            Ordinal = MftRecordParser.VolumeRootRecord,
            Name = ".",
        });
    }

    /// <summary>
    /// True once record 5 itself has been read. Every path on the volume starts there, so a
    /// root that was never read is a decline, not an empty tree.
    /// </summary>
    public bool HasVolumeRoot => _volumeRootRead;

    /// <summary>Directories retained, for the fallback reason when the map fills up.</summary>
    public int Count => _all.Count;

    public bool MemoryLimitReached { get; private set; }

    /// <summary>
    /// Records one directory under the one name it carries. Returns false once the map is
    /// full, which the backend turns into a fallback rather than a partial directory set.
    /// </summary>
    public bool TryAdd(MftRecordInfo record, MftName name)
    {
        if (MemoryLimitReached)
        {
            return false;
        }

        if (!_directories.TryGetValue(record.Ordinal, out Node? node))
        {
            if (_all.Count >= _limit)
            {
                return false;
            }

            if (!ReserveMemory(512L + (2L * name.Name.Length)))
            {
                return false;
            }

            node = new Node { Ordinal = record.Ordinal };
            _directories.Add(record.Ordinal, node);
            _all.Add(node);
        }

        node.Sequence = record.SequenceNumber;
        node.Attributes = record.Attributes;
        node.IsReparsePoint = record.IsReparsePoint;
        node.ParentOrdinal = name.ParentOrdinal;
        node.ParentSequence = name.ParentSequence;
        node.Name = name.Name;

        if (record.Ordinal == MftRecordParser.VolumeRootRecord)
        {
            _volumeRootRead = true;
        }

        return true;
    }

    /// <summary>
    /// Walks the caller's path from the volume root and returns the record number of the
    /// directory it names, checking each step against the generation of its parent.
    /// </summary>
    public bool TryResolveRoot(string[] segments, out ulong rootOrdinal, out string? failure)
    {
        rootOrdinal = 0;

        if (!_volumeRootRead)
        {
            failure = "the volume's root record could not be read.";
            return false;
        }

        BuildChildrenIndex();

        ulong current = MftRecordParser.VolumeRootRecord;
        string walked = string.Empty;

        foreach (string segment in segments)
        {
            string display = walked.Length == 0 ? segment : string.Concat(walked, "\\", segment);
            Node? match = FindChild(current, segment);

            if (match is null)
            {
                failure = $"'{display}' is not a directory in this volume.";
                return false;
            }

            if (match.IsReparsePoint)
            {
                failure = "The scan root is a link. Scan the directory it points to instead.";
                return false;
            }

            if (!_directories.TryGetValue(current, out Node? holder) || match.ParentSequence != holder.Sequence)
            {
                failure = $"'{display}' refers to a generation of its parent that no longer exists.";
                return false;
            }

            current = match.Ordinal;
            walked = display;
        }

        rootOrdinal = current;
        failure = null;
        return true;
    }

    /// <summary>
    /// Marks the directories at or below <paramref name="rootOrdinal"/>, refusing to cross a
    /// link, a stale reference, a skipped directory or the depth limit.
    /// </summary>
    /// <remarks>
    /// A directory reached through a link is unvisited, not merely unfollowed: its children
    /// are reachable only through it, so leaving it out takes its whole subtree. The link
    /// itself is still emitted as a zero-byte node, which is what the portable backend does.
    /// </remarks>
    public Exclusions MarkSubtree(ulong rootOrdinal, ScanOptions options, CancellationToken cancellationToken)
    {
        Exclusions exclusions = new();
        BuildChildrenIndex();

        if (!_directories.TryGetValue(rootOrdinal, out Node? root))
        {
            return exclusions;
        }

        root.InSubtree = true;
        root.Depth = 0;
        root.RelativePath = string.Empty;

        Queue<Node> queue = new();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Node parent = queue.Dequeue();
            if (!_children!.TryGetValue(parent.Ordinal, out List<Node>? children))
            {
                continue;
            }

            int childDepth = parent.Depth + 1;

            foreach (Node child in children)
            {
                // Already reached. This is also how a cycle stops: a directory that is its
                // own ancestor is not visited twice, and the walk terminates.
                if (child.InSubtree)
                {
                    continue;
                }

                if (child.ParentSequence != parent.Sequence)
                {
                    exclusions.StaleDirectories++;
                    continue;
                }

                if (child.IsReparsePoint)
                {
                    exclusions.LinksNotFollowed++;
                    continue;
                }

                if ((child.Attributes & options.SkipAttributes) != 0)
                {
                    continue;
                }

                if (childDepth > options.MaxDepth)
                {
                    exclusions.BeyondDepth++;
                    continue;
                }

                long pathLength = parent.RelativePath.Length + 1L + child.Name.Length;
                if (!ReserveMemory(512L + (4L * pathLength)))
                {
                    return exclusions;
                }

                child.InSubtree = true;
                child.Depth = childDepth;
                child.RelativePath = ScanPath.Join(parent.RelativePath, child.Name);
                queue.Enqueue(child);
            }
        }

        return exclusions;
    }

    private bool ReserveMemory(long bytes)
    {
        if (_collector is null || _collector.TryReserveMemory(bytes))
        {
            return true;
        }

        MemoryLimitReached = true;
        return false;
    }

    /// <summary>
    /// Resolves one link's parent reference.
    /// </summary>
    /// <remarks>
    /// A record that expects a different generation of its parent is not attached to whatever
    /// now occupies that record number. Inventing a location for the file moves a directory's
    /// contents under an unrelated tree; leaving the entry out and saying so does not.
    /// </remarks>
    public MftParentStatus ResolveParent(
        MftName name,
        ulong childOrdinal,
        out bool inSubtree,
        out int parentDepth,
        out string relativePrefix)
    {
        inSubtree = false;
        parentDepth = 0;
        relativePrefix = string.Empty;

        if (!_directories.TryGetValue(name.ParentOrdinal, out Node? parent))
        {
            return MftParentStatus.UnknownParent;
        }

        if (parent.Ordinal == childOrdinal && childOrdinal != MftRecordParser.VolumeRootRecord)
        {
            return MftParentStatus.CyclicParent;
        }

        if (parent.Sequence != name.ParentSequence)
        {
            return MftParentStatus.StaleParent;
        }

        inSubtree = parent.InSubtree;
        parentDepth = parent.Depth;
        relativePrefix = parent.RelativePath;
        return MftParentStatus.Resolved;
    }

    private Node? FindChild(ulong parentOrdinal, string name)
    {
        if (!_children!.TryGetValue(parentOrdinal, out List<Node>? candidates))
        {
            return null;
        }

        foreach (Node candidate in candidates)
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private void BuildChildrenIndex()
    {
        if (_children is not null)
        {
            return;
        }

        Dictionary<ulong, List<Node>> children = [];

        foreach (Node node in _all)
        {
            if (node.Ordinal == MftRecordParser.VolumeRootRecord)
            {
                continue;
            }

            if (!children.TryGetValue(node.ParentOrdinal, out List<Node>? siblings))
            {
                siblings = [];
                children.Add(node.ParentOrdinal, siblings);
            }

            siblings.Add(node);
        }

        _children = children;
    }
}
