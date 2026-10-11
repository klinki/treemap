namespace Treemap;

/// <summary>
/// Node categories. Free space, unknown space and missing data are modelled as
/// first-class tree members so the renderer can draw them as treemap blocks.
/// </summary>
public enum NodeKind
{
    Drive,
    Folder,
    File,

    /// <summary>Unallocated space on the volume, rendered as a block.</summary>
    FreeSpace,

    /// <summary>Used volume space not represented by scanned files, including metadata and allocation differences.</summary>
    UnknownSpace,

    /// <summary>Space that is neither free nor accounted for (deallocated clusters).</summary>
    MissingData,
}

/// <summary>
/// An immutable node in a treemap tree. Instances are never mutated after
/// construction, which is what makes a published snapshot safe to consume from
/// the render thread without copying or locking.
/// </summary>
public sealed class TreemapNode
{
    private static readonly IReadOnlyList<TreemapNode> NoChildren = Array.AsReadOnly(Array.Empty<TreemapNode>());

    public int Id { get; }

    public string Name { get; }

    public NodeKind Kind { get; }

    /// <summary>Apparent size in bytes.</summary>
    public long LogicalSize { get; }

    /// <summary>Allocated size in bytes. May exceed <see cref="LogicalSize"/> for sparse files.</summary>
    public long PhysicalSize { get; }

    public DateTimeOffset? LastWriteTime { get; }

    /// <summary>Index into the colour table. Grouped by file type by convention.</summary>
    public int ColorGroup { get; }

    public IReadOnlyList<TreemapNode> Children { get; }

    /// <summary>True when this node's children are split as free space rather than folders/files.</summary>
    public bool IsSpace => Kind is NodeKind.FreeSpace or NodeKind.UnknownSpace or NodeKind.MissingData;

    private TreemapNode(
        int id,
        string name,
        NodeKind kind,
        long logicalSize,
        long physicalSize,
        DateTimeOffset? lastWriteTime,
        int colorGroup,
        IReadOnlyList<TreemapNode> children)
    {
        Id = id;
        Name = name;
        Kind = kind;
        LogicalSize = logicalSize;
        PhysicalSize = physicalSize;
        LastWriteTime = lastWriteTime;
        ColorGroup = colorGroup;
        Children = children;
    }

    /// <summary>
    /// Creates a node. When <paramref name="logicalSize"/> / <paramref name="physicalSize"/>
    /// are omitted for a node with children, they are computed by summing descendants,
    /// so a partially-built tree can never silently render as zero-area.
    /// </summary>
    /// <remarks>
    /// The caller's child collection is copied. Nodes are treated as immutable the
    /// moment they are built, and a published snapshot is walked from the render thread
    /// without any lock — sharing a mutable backing list or array would reintroduce the
    /// exact race the snapshot design exists to remove.
    /// </remarks>
    public static TreemapNode Create(
        int id,
        string name,
        NodeKind kind = NodeKind.File,
        long logicalSize = 0,
        long physicalSize = 0,
        DateTimeOffset? lastWriteTime = null,
        int colorGroup = 0,
        IReadOnlyList<TreemapNode>? children = null)
    {
        IReadOnlyList<TreemapNode> kids = Freeze(children);

        if (kids.Count > 0 && logicalSize == 0 && physicalSize == 0)
        {
            long childLogical = 0;
            long childPhysical = 0;
            foreach (TreemapNode child in kids)
            {
                // Logical bytes and allocated bytes are separate totals.
                childLogical += child.LogicalSize;
                childPhysical += child.PhysicalSize;
            }

            logicalSize = childLogical;
            physicalSize = childPhysical;
        }

        return new TreemapNode(id, name, kind, logicalSize, physicalSize, lastWriteTime, colorGroup, kids);
    }

    /// <summary>
    /// Copies <paramref name="children"/> into a read-only wrapper so neither the
    /// caller's collection nor the copy's backing array is reachable from the node.
    /// </summary>
    private static IReadOnlyList<TreemapNode> Freeze(IReadOnlyList<TreemapNode>? children)
    {
        if (children is null || children.Count == 0)
        {
            return NoChildren;
        }

        TreemapNode[] copy = new TreemapNode[children.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = children[i];
        }

        return Array.AsReadOnly(copy);
    }

    /// <summary>
    /// The size used for layout. Physical size is preferred when it is known and
    /// positive, because a treemap answers "what is consuming the disk".
    /// </summary>
    public long LayoutSize => PhysicalSize > 0 ? PhysicalSize : LogicalSize;

    /// <summary>
    /// Returns a copy of <paramref name="node"/> with sizes summed from descendants.
    /// Optional overrides replace the computed values (used for free/unknown space,
    /// which is reported by the volume rather than by any child).
    /// </summary>
    /// <remarks>
    /// Each node is aggregated exactly once and an unchanged subtree is returned as the
    /// same instance, so cost is linear in tree size and callers holding an older node
    /// reference still see reference equality when nothing moved.
    /// </remarks>
    public static TreemapNode WithAggregatedSizes(
        TreemapNode node,
        long? logicalSizeOverride = null,
        long? physicalSizeOverride = null)
    {
        int childCount = node.Children.Count;

        // Non-null only when at least one child instance was replaced. Wrapped, never a
        // bare array: the rebuilt list is handed to a node and must stay unmodifiable.
        IReadOnlyList<TreemapNode>? rebuiltChildren = null;
        long childLogical = 0;
        long childPhysical = 0;

        if (childCount > 0)
        {
            TreemapNode[] rebuilt = new TreemapNode[childCount];
            bool anyChildChanged = false;

            for (int i = 0; i < childCount; i++)
            {
                TreemapNode child = node.Children[i];
                TreemapNode aggregated = WithAggregatedSizes(child);

                rebuilt[i] = aggregated;
                childLogical += aggregated.LogicalSize;
                childPhysical += aggregated.PhysicalSize;
                anyChildChanged |= !ReferenceEquals(child, aggregated);
            }

            if (anyChildChanged)
            {
                rebuiltChildren = Array.AsReadOnly(rebuilt);
            }
        }

        long logical = logicalSizeOverride ?? (childCount > 0 ? childLogical : node.LogicalSize);
        long physical = physicalSizeOverride ?? (childCount > 0 ? childPhysical : node.PhysicalSize);

        if (rebuiltChildren is null && logical == node.LogicalSize && physical == node.PhysicalSize)
        {
            return node;
        }

        return new TreemapNode(
            node.Id, node.Name, node.Kind, logical, physical,
            node.LastWriteTime, node.ColorGroup, rebuiltChildren ?? node.Children);
    }

    public override string ToString() => $"{Kind} '{Name}' ({LayoutSize} bytes)";
}
