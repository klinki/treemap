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

    /// <summary>Space that exists but has not been scanned yet.</summary>
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
    private static readonly IReadOnlyList<TreemapNode> NoChildren = Array.Empty<TreemapNode>();

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
        IReadOnlyList<TreemapNode> kids = children ?? NoChildren;

        if (kids.Count > 0 && logicalSize == 0 && physicalSize == 0)
        {
            long childLogical = 0;
            long childPhysical = 0;
            foreach (TreemapNode child in kids)
            {
                childLogical += child.LayoutSize;
                childPhysical += child.PhysicalSize;
            }

            logicalSize = childLogical;
            physicalSize = childPhysical;
        }

        return new TreemapNode(id, name, kind, logicalSize, physicalSize, lastWriteTime, colorGroup, kids);
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
    public static TreemapNode WithAggregatedSizes(
        TreemapNode node,
        long? logicalSizeOverride = null,
        long? physicalSizeOverride = null)
    {
        long logical;
        long physical;

        if (node.Children.Count == 0)
        {
            logical = logicalSizeOverride ?? node.LogicalSize;
            physical = physicalSizeOverride ?? node.PhysicalSize;
        }
        else
        {
            long childLogical = 0;
            long childPhysical = 0;
            foreach (TreemapNode child in node.Children)
            {
                TreemapNode aggregated = WithAggregatedSizes(child);
                childLogical += aggregated.LogicalSize;
                childPhysical += aggregated.PhysicalSize;
            }

            logical = logicalSizeOverride ?? childLogical;
            physical = physicalSizeOverride ?? childPhysical;
        }

        if (logical == node.LogicalSize && physical == node.PhysicalSize && ChildrenSame(node))
        {
            return node;
        }

        TreemapNode[] rebuilt;
        if (node.Children.Count == 0)
        {
            rebuilt = NoChildren.ToArray();
        }
        else
        {
            rebuilt = new TreemapNode[node.Children.Count];
            for (int i = 0; i < node.Children.Count; i++)
            {
                rebuilt[i] = WithAggregatedSizes(node.Children[i]);
            }
        }

        return new TreemapNode(
            node.Id, node.Name, node.Kind, logical, physical,
            node.LastWriteTime, node.ColorGroup, rebuilt);
    }

    private static bool ChildrenSame(TreemapNode node) => node.Children.Count == 0;

    public override string ToString() => $"{Kind} '{Name}' ({LayoutSize} bytes)";
}
